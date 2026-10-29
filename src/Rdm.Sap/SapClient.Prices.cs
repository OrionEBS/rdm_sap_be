using System.Collections.Concurrent;
using Polly.CircuitBreaker;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

using Row = IReadOnlyDictionary<string, object?>;

/// <summary>
/// Bảng giá ZVLP / ZVDI / ZMIN / ZVIV qua Z_ESD015_PRICE_UPLOAD (Sap:PriceConditionFunction, source abap/Z_ESD015_PRICE_UPLOAD.abap)
/// — RFC của report upload giá ZCSD002_VN_PRICING_UPLD, chạy VK11 / VK12 bằng BDC:
///   C = tạo (VK11) · U = sửa giá / đơn vị / hiệu lực đến (VK12) · R = duyệt bản chờ duyệt (VK12 processing status 02, như ZSD_VN_PRICE_APR).
/// Đã chọn thay BAPI_PRICES_CONDITIONS (không remote-enabled, chưa dùng ở đâu trên DRF-930).
///
/// Trạng thái duyệt: CALL TRANSACTION là batch input nên enhancement ZESD004_DEFAULT_STAT không gán KFRST = A → bảng giá tạo / sửa
/// qua API có hiệu lực ngay (duyệt đã làm trên RDM). R chỉ dùng cho bảng giá chờ duyệt nhập tay trên SAP.
///
/// Mọi kiểm tra (khóa, hiệu lực, dấu giá, trùng / chồng hiệu lực) chạy trước khi ghi: có dòng lỗi → không ghi gì.
/// Z_ESD015 commit từng dòng → SAP lỗi vài dòng thì các dòng khác đã ghi (PRICE_PARTIAL). Gửi lại cả lô vẫn an toàn: dòng đã tạo bị
/// chặn bởi PRICE_SAME_PERIOD_EXISTS ở bước kiểm tra.
/// Chồng hiệu lực xét theo dòng bảng Axxx (record bị SAP tách có nhiều dòng cùng KNUMH), bỏ qua record đã xóa (KONP-LOEVM_KO).
/// </summary>
public sealed partial class SapClient
{
    private readonly ConcurrentDictionary<string, int> _currencyDecimals = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ đọc

    public Task<PriceListResponse> GetPricesAsync(PriceQuery query, CancellationToken ct) =>
        Task.Run(() =>
        {
            var key = PriceConditionRules.BuildKey(ToKeyInput(query), allowPartial: true, out var error);
            if (key is null) throw new ArgumentException(error!.Value.Message);

            var tables = string.IsNullOrWhiteSpace(query.Incoterms) && string.IsNullOrWhiteSpace(query.Region)
                ? PriceConditionRules.TablesFor(key.ConditionType)
                : new[] { key.Table };
            try
            {
                // Đọc không có tác dụng phụ → bọc cả lần đọc trong Polly (retry + circuit breaker).
                var items = _resilience.Connect.Execute(() =>
                {
                    var dest = _destinations.Get();
                    return tables.SelectMany(t => ReadPriceRecords(dest, key with { Table = t }, query.ValidFrom, query.ValidTo,
                            query.PendingApproval switch { true => "A", false => "", _ => null }))
                        .Where(r => query.IncludeDeleted || !r.Deleted)
                        .OrderBy(r => r.Key.Table.Number, StringComparer.Ordinal)
                        .ThenBy(r => r.Key.VarKey, StringComparer.Ordinal).ThenBy(r => r.ValidFrom)
                        .Select(ToDto)
                        .ToList();
                });
                return new PriceListResponse(key.ConditionType, SapRequestMapper.Code(query.SalesOrganization)!,
                    query.ValidFrom!.Value, query.ValidTo!.Value, items.Count,
                    tables.Select(PriceConditionRules.Layout).ToList(), items);
            }
            catch (Exception ex) when (ex is RfcBaseException or BrokenCircuitException)
            {
                var code = ex switch
                {
                    RfcLogonException => SapErrorCodes.Logon,
                    RfcCommunicationException or RfcResourceException or BrokenCircuitException => SapErrorCodes.Unavailable,
                    _ => SapErrorCodes.Business
                };
                throw new SapReadException(code, RfcTableReader.FunctionName, ex.Message, ex);
            }
        }, ct);

    // ------------------------------------------------------------------ tạo / kiểm tra / copy VAT

    public Task<SapCallResult> CheckPricesAsync(PriceCreateRequest request, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                var dest = _resilience.Connect.Execute(() => _destinations.Get());
                var (lines, messages) = ValidateCreate(dest, request.Items, request.OverlapMode);
                return lines is null
                    ? ValidationFailed(messages)
                    : SapCallResult.Success(new SapDocumentRef(null, ObjectType: "KONH"),
                        messages.Concat(lines.Select(l => ItemMessage("S", PriceErrorCodes.Ok, l.Index, "Hợp lệ — chưa ghi SAP."))).ToList());
            }
            catch (Exception ex)
            {
                return ClassifyBeforeSend("PriceCheck", ex);
            }
        }, ct);

    public Task<SapCallResult> CreatePricesAsync(PriceCreateRequest request, CancellationToken ct) =>
        RunAsync(_options.PriceConditionFunction, stateful: false, (dest, ctx) =>
        {
            var (lines, messages) = ValidateCreate(dest, request.Items, request.OverlapMode);
            return lines is null ? ValidationFailed(messages) : CreateCore(dest, ctx, lines, messages, sourceInfo: null);
        }, ct);

    public Task<SapCallResult> CopyPricesWithVatAsync(PriceCopyWithVatRequest request, CancellationToken ct) =>
        RunAsync(_options.PriceConditionFunction, stateful: false, (dest, ctx) =>
        {
            var key = PriceConditionRules.BuildKey(request.Source, allowPartial: true, out var keyError);
            if (key is null) return ValidationFailed(new() { ItemMessage("E", keyError!.Value.Code, 0, keyError.Value.Message) });

            var validFrom = request.ValidFrom!.Value;
            var sourceDate = request.SourceDate ?? validFrom.AddDays(-1);
            var tables = string.IsNullOrWhiteSpace(request.Source.Incoterms) && string.IsNullOrWhiteSpace(request.Source.Region)
                ? PriceConditionRules.TablesFor(key.ConditionType)
                : new[] { key.Table };
            var sources = tables
                .SelectMany(t => ReadPriceRecords(dest, key with { Table = t }, sourceDate, sourceDate, releaseStatus: ""))
                .Where(r => !r.Deleted && r.Amount is not null)
                .OrderBy(r => r.Key.VarKey, StringComparer.Ordinal)
                .ToList();

            if (sources.Count == 0)
                return ValidationFailed(new()
                {
                    ItemMessage("E", PriceErrorCodes.SourceNotFound, 0,
                        $"Không có bảng giá {key.ConditionType} đã duyệt còn hiệu lực ngày {sourceDate:dd.MM.yyyy} khớp bộ lọc.")
                });
            if (sources.Count > PriceCopyWithVatRequest.MaxSources)
                return ValidationFailed(new()
                {
                    ItemMessage("E", PriceErrorCodes.TooManySources, 0,
                        $"{sources.Count} bảng giá nguồn (tối đa {PriceCopyWithVatRequest.MaxSources}) — thu hẹp bộ lọc.")
                });

            var items = sources.Select(s => new PriceCreateItem
            {
                ConditionType = s.Key.ConditionType,
                SalesOrganization = s.Key.Values.GetValueOrDefault("VKORG") ?? s.Key.Values.GetValueOrDefault("VKORGAU"),
                DistributionChannel = s.Key.Values.GetValueOrDefault("VTWEG"),
                SoldTo = s.Key.Values.GetValueOrDefault("KUNAG"),
                ShipTo = s.Key.Values.GetValueOrDefault("KUNWE"),
                Material = s.Key.Values.GetValueOrDefault("MATNR"),
                ShippingCondition = s.Key.Values.GetValueOrDefault("VSBED"),
                Plant = s.Key.Values.GetValueOrDefault("WERKS"),
                Incoterms = s.Key.Values.GetValueOrDefault("INCO1"),
                Region = s.Key.Values.GetValueOrDefault("REGIO"),
                Amount = PriceConditionRules.ApplyVat(s.Amount!.Value, request.VatPercent, request.OldVatPercent, request.RoundingUnit),
                Currency = s.Currency ?? "VND",
                PricingUnit = s.PricingUnit ?? 1,
                Unit = s.Unit ?? string.Empty,
                ValidFrom = validFrom,
                ValidTo = request.ValidTo ?? (s.ValidTo >= validFrom ? s.ValidTo : PriceConditionRules.MaxDate)
            }).ToList();

            var (lines, messages) = ValidateCreate(dest, items, request.OverlapMode);
            var sourceInfo = sources.Select((s, i) => (Index: i + 1, s)).ToDictionary(x => x.Index, x => x.s);
            if (lines is null) return ValidationFailed(messages.Select(m => WithSource(m, sourceInfo)).ToList());
            return CreateCore(dest, ctx, lines, messages, sourceInfo);
        }, ct);

    // ------------------------------------------------------------------ sửa

    public Task<SapCallResult> ChangePricesAsync(PriceChangeRequest request, CancellationToken ct) =>
        RunAsync(_options.PriceConditionFunction, stateful: false, (dest, ctx) =>
        {
            var messages = new List<SapReturnMessage>();
            var rows = new List<Row>();
            var knumhByRow = new Dictionary<int, string>();
            var planned = new List<(int Index, PriceKey Key, DateOnly From, DateOnly To)>();
            var seen = new HashSet<string>();

            for (var i = 0; i < request.Items.Count; i++)
            {
                var index = i + 1;
                var item = request.Items[i];
                var knumh = AccDocumentMapper.Alpha(item.ConditionRecordNumber, 10)!;
                if (!seen.Add(knumh))
                {
                    messages.Add(ItemMessage("E", PriceErrorCodes.DuplicateInRequest, index, $"KNUMH {knumh} bị lặp trong request."));
                    continue;
                }

                if (item.Amount is null && item.PricingUnit is null && item.Unit is null && item.ValidTo is null)
                {
                    messages.Add(ItemMessage("E", PriceErrorCodes.NoChange, index, $"Dòng {index}: không có field nào để sửa."));
                    continue;
                }

                var periods = ReadRecordByNumber(dest, knumh, out var notFound);
                if (periods is null)
                {
                    messages.Add(ItemMessage("E", notFound!.Value.Code, index, notFound.Value.Message));
                    continue;
                }
                var current = periods[0];
                if (current.Deleted)
                {
                    messages.Add(ItemMessage("E", PriceErrorCodes.Deleted, index, $"Bảng giá {knumh} đã bị đánh dấu xóa."));
                    continue;
                }

                // Kỳ được sửa hiệu lực đến: PeriodStart chọn kỳ khi record bị tách; không tách thì là kỳ duy nhất.
                var period = item.PeriodStart is { } start ? periods.FirstOrDefault(p => p.ValidFrom == start) : periods[0];
                if (item.ValidTo is not null && (period is null || (periods.Count > 1 && item.PeriodStart is null)))
                {
                    messages.Add(ItemMessage("E", PriceErrorCodes.SplitRecord, index,
                        $"Bảng giá {knumh} có {periods.Count} kỳ hiệu lực (" +
                        string.Join(", ", periods.Select(p => $"{p.ValidFrom:dd.MM.yyyy}–{p.ValidTo:dd.MM.yyyy}")) +
                        ") — truyền PeriodStart = ngày bắt đầu của kỳ cần đổi hiệu lực đến."));
                    continue;
                }
                period ??= periods[0];

                if (item.ValidTo is { } to && to < period.ValidFrom)
                {
                    messages.Add(ItemMessage("E", PriceErrorCodes.InvalidPeriod, index,
                        $"Hiệu lực đến {to:dd.MM.yyyy} trước hiệu lực từ {period.ValidFrom:dd.MM.yyyy} của kỳ."));
                    continue;
                }
                if (item.Amount is { } amount && !PriceConditionRules.AmountSignValid(current.Key.ConditionType, amount))
                {
                    messages.Add(ItemMessage("E", PriceErrorCodes.InvalidAmount, index, AmountRule(current.Key.ConditionType)));
                    continue;
                }

                if (item.ValidTo is { } newTo && newTo > period.ValidTo)
                {
                    // Chỉ phần kéo dài mới có thể chồng lên bản khác; rút ngắn thì không.
                    var others = ReadPriceRecords(dest, current.Key, period.ValidTo.AddDays(1), newTo, releaseStatus: null);
                    foreach (var (type, code, text) in PriceConditionRules.CheckAgainstExisting(
                                 current.Key, period.ValidFrom, newTo, others, request.OverlapMode, ignoreRecord: knumh))
                        messages.Add(ItemMessage(type, code, index, text));
                    planned.Add((index, current.Key, period.ValidFrom, newTo));
                }

                rows.Add(PriceConditionRules.ToChangeRow(index, knumh, item.Amount, item.PricingUnit, item.Unit, item.ValidTo,
                    periods.Count > 1 ? period.ValidFrom : null));
                knumhByRow[index] = knumh;
                messages.Add(ItemMessage("I", "PRICE_PLAN", index,
                    $"{knumh}: " + string.Join(", ", new[]
                    {
                        item.Amount is null ? null : $"giá {current.Amount:0.##} → {item.Amount:0.##}",
                        item.PricingUnit is null ? null : $"pricing unit {current.PricingUnit} → {item.PricingUnit}",
                        item.Unit is null ? null : $"đơn vị {current.Unit} → {SapRequestMapper.Code(item.Unit)}",
                        item.ValidTo is null ? null : $"hiệu lực đến {period.ValidTo:dd.MM.yyyy} → {item.ValidTo:dd.MM.yyyy}"
                    }.Where(x => x is not null)) + ".", v1: knumh));
            }

            foreach (var (index, text) in PriceConditionRules.CheckWithinRequest(planned))
                messages.Add(ItemMessage("E", PriceErrorCodes.DuplicateInRequest, index, text));
            if (messages.Any(m => m.IsError)) return ValidationFailed(messages);

            var call = CallUpload(dest, ctx, "U", null, rows);
            if (call.Error is not null) return call.Error with { Messages = messages.Concat(call.Error.Messages).ToList() };

            var all = new List<SapReturnMessage>(messages);
            all.AddRange(call.Messages);
            foreach (var (index, knumh) in knumhByRow)
                all.Add(call.FailedRows.Contains(index)
                    ? ItemMessage("E", "PRICE_NOT_CHANGED", index, $"Bảng giá {knumh} chưa sửa được — xem message SAP của dòng.", v1: knumh)
                    : ItemMessage("S", PriceErrorCodes.Ok, index, $"Đã sửa bảng giá {knumh}.", v1: knumh));
            return RowsResult(all, knumhByRow.Count, call.FailedRows.Count,
                request.Items.Count == 1 ? knumhByRow.Values.FirstOrDefault() : null);
        }, ct);

    // ------------------------------------------------------------------ duyệt

    public Task<SapCallResult> ApprovePricesAsync(PriceApproveRequest request, CancellationToken ct) =>
        RunAsync(_options.PriceConditionFunction, stateful: false, (dest, ctx) =>
        {
            var messages = new List<SapReturnMessage>();
            var toRelease = new List<string>();

            foreach (var raw in request.ConditionRecordNumbers.Select(n => AccDocumentMapper.Alpha(n, 10)!).Distinct())
            {
                var periods = ReadRecordByNumber(dest, raw, out var notFound);
                if (periods is null)
                {
                    messages.Add(KnumhMessage("E", notFound!.Value.Code, raw, notFound.Value.Message));
                    continue;
                }
                if (periods[0].Deleted)
                {
                    messages.Add(KnumhMessage("E", PriceErrorCodes.Deleted, raw, $"Bảng giá {raw} đã bị đánh dấu xóa."));
                    continue;
                }
                var pending = periods.Where(r => r.Pending).ToList();
                if (pending.Count == 0)
                {
                    messages.Add(KnumhMessage("I", PriceErrorCodes.AlreadyApproved, raw, $"Bảng giá {raw} đã duyệt — bỏ qua."));
                    continue;
                }

                foreach (var p in pending)
                {
                    var released = ReadPriceRecords(dest, p.Key, p.ValidFrom, p.ValidTo, releaseStatus: "");
                    foreach (var (_, code, text) in PriceConditionRules.CheckAgainstExisting(
                                 p.Key, p.ValidFrom, p.ValidTo, released, PriceOverlapMode.Replace, ignoreRecord: raw))
                        messages.Add(KnumhMessage("W", code, raw, text));
                }
                toRelease.Add(raw);
            }

            if (messages.Any(m => m.IsError)) return ValidationFailed(messages);
            if (toRelease.Count == 0)
                return SapCallResult.Success(new SapDocumentRef(null, ObjectType: "KONH"), messages);

            var call = CallUpload(dest, ctx, "R", null,
                toRelease.Select((knumh, i) => PriceConditionRules.ToReleaseRow(i + 1, knumh)).ToList());
            if (call.Error is not null) return call.Error with { Messages = messages.Concat(call.Error.Messages).ToList() };
            // ROW_NO = vị trí trong toRelease → gắn message SAP về KNUMH.
            messages.AddRange(call.Messages.Select(m => int.TryParse(m.Item, out var row) && row >= 1 && row <= toRelease.Count
                ? m with { Item = toRelease[row - 1], MessageV1 = m.MessageV1 ?? toRelease[row - 1] } : m));

            // Xác nhận lại trên SAP: KFRST phải hết A. Duyệt là idempotent nên lỗi từng phần trả Failed (gửi lại an toàn).
            var failed = new List<string>();
            foreach (var knumh in toRelease)
            {
                var after = ReadRecordByNumber(dest, knumh, out _);
                if (after is null || after.Any(r => r.Pending))
                {
                    failed.Add(knumh);
                    messages.Add(KnumhMessage("E", "PRICE_NOT_RELEASED", knumh, $"Bảng giá {knumh} vẫn ở trạng thái chờ duyệt sau khi gọi SAP."));
                }
                else
                {
                    messages.Add(KnumhMessage("S", PriceErrorCodes.Ok, knumh, $"Bảng giá {knumh} đã duyệt."));
                }
            }
            return failed.Count == 0
                ? SapCallResult.Success(new SapDocumentRef(toRelease.Count == 1 ? toRelease[0] : null, ObjectType: "KONH"), messages)
                : SapCallResult.Failed(SapErrorCodes.Business,
                    $"{failed.Count}/{toRelease.Count} bảng giá chưa duyệt được ({string.Join(", ", failed)}). Gửi lại an toàn — bản đã duyệt sẽ được bỏ qua.",
                    messages);
        }, ct);

    // ------------------------------------------------------------------ helpers

    /// <summary>Kiểm tra toàn bộ dòng tạo mới; trả (null, messages) nếu có lỗi.</summary>
    private (List<PriceCreateLine>? Lines, List<SapReturnMessage> Messages) ValidateCreate(
        RfcDestination dest, IReadOnlyList<PriceCreateItem> items, PriceOverlapMode mode)
    {
        var messages = new List<SapReturnMessage>();
        var lines = ValidateCreateInput(items, messages);

        foreach (var (index, text) in PriceConditionRules.CheckWithinRequest(lines.Select(l => (l.Index, l.Key, l.ValidFrom, l.ValidTo)).ToList()))
            messages.Add(ItemMessage("E", PriceErrorCodes.DuplicateInRequest, index, text));

        CheckOverlapWithExisting(dest, lines, mode, messages);

        return (messages.Any(m => m.IsError) ? null : lines, messages);
    }

    /// <summary>Kiểm tra đầu vào từng dòng tạo mới (khóa, hiệu lực, dấu giá) — không gọi SAP. Trả các dòng hợp lệ.</summary>
    private static List<PriceCreateLine> ValidateCreateInput(IReadOnlyList<PriceCreateItem> items, List<SapReturnMessage> messages)
    {
        var lines = new List<PriceCreateLine>();
        for (var i = 0; i < items.Count; i++)
        {
            var index = i + 1;
            var item = items[i];
            var key = PriceConditionRules.BuildKey(item, allowPartial: false, out var keyError);
            if (key is null)
            {
                messages.Add(ItemMessage("E", keyError!.Value.Code, index, keyError.Value.Message));
                continue;
            }
            var from = item.ValidFrom!.Value;
            var to = item.ValidTo ?? PriceConditionRules.MaxDate;
            if (to < from)
            {
                messages.Add(ItemMessage("E", PriceErrorCodes.InvalidPeriod, index, "Hiệu lực đến phải ≥ hiệu lực từ."));
                continue;
            }
            if (!PriceConditionRules.AmountSignValid(key.ConditionType, item.Amount))
            {
                messages.Add(ItemMessage("E", PriceErrorCodes.InvalidAmount, index, AmountRule(key.ConditionType)));
                continue;
            }
            lines.Add(new PriceCreateLine(index, key, item, from, to));
        }
        return lines;
    }

    /// <summary>
    /// Kiểm tra chồng hiệu lực với bảng giá có sẵn trên SAP — đọc theo lô (vật tư / sales org) cho toàn bộ dòng,
    /// trong khoảng bao trùm mọi dòng; CheckAgainstExisting tự lọc theo kỳ của từng dòng.
    /// </summary>
    private void CheckOverlapWithExisting(
        RfcDestination dest, IReadOnlyList<PriceCreateLine> lines, PriceOverlapMode mode, List<SapReturnMessage> messages)
    {
        if (lines.Count == 0) return;
        var existing = ReadPriceRecordsBulk(dest, lines.Select(l => l.Key), lines.Min(l => l.ValidFrom), lines.Max(l => l.ValidTo));
        foreach (var l in lines)
        {
            var records = existing.GetValueOrDefault(l.Key.Identity) ?? new List<PriceRecord>();
            foreach (var (type, code, text) in PriceConditionRules.CheckAgainstExisting(l.Key, l.ValidFrom, l.ValidTo, records, mode))
                messages.Add(ItemMessage(type, code, l.Index, text));
        }
    }

    /// <summary>VK11 qua Z_ESD015 (1 lần gọi / loại điều kiện), rồi đọc lại Axxx để lấy KNUMH của từng dòng đã tạo.</summary>
    private SapCallResult CreateCore(
        RfcDestination dest, CallContext ctx, List<PriceCreateLine> lines,
        List<SapReturnMessage> messages, IReadOnlyDictionary<int, PriceRecord>? sourceInfo)
    {
        var all = new List<SapReturnMessage>(messages);
        var failed = new HashSet<int>();
        foreach (var group in lines.GroupBy(l => l.Key.ConditionType))
        {
            var call = CallUpload(dest, ctx, "C", group.Key, group.Select(PriceConditionRules.ToUploadRow).ToList());
            if (call.Error is not null)
            {
                // Lỗi trước khi SAP ghi dòng nào của nhóm này; các nhóm trước (nếu có) đã ghi → báo từng phần.
                if (failed.Count == 0 && all.Count == messages.Count)
                    return call.Error with { Messages = all.Concat(call.Error.Messages).ToList() };
                all.AddRange(call.Error.Messages);
                foreach (var l in group) failed.Add(l.Index);
                continue;
            }
            all.AddRange(call.Messages);
            failed.UnionWith(call.FailedRows);
        }

        string? single = null;
        foreach (var l in lines)
        {
            var source = sourceInfo?.GetValueOrDefault(l.Index);
            if (failed.Contains(l.Index))
            {
                all.Add(new SapReturnMessage("E", "RDM", "PRICE_NOT_CREATED",
                    $"Dòng {l.Index} chưa tạo được — xem message SAP của dòng.", MessageV2: source?.ConditionRecordNumber, Item: l.Index.ToString()));
                continue;
            }
            var knumh = ReadPriceRecords(dest, l.Key, l.ValidFrom, l.ValidTo, releaseStatus: null)
                .FirstOrDefault(r => !r.Deleted && r.ValidFrom == l.ValidFrom && r.ValidTo == l.ValidTo)?.ConditionRecordNumber;
            single ??= knumh;
            all.Add(new SapReturnMessage("S", "RDM", PriceErrorCodes.Ok,
                $"Tạo bảng giá {knumh ?? "(chưa đọc được số KNUMH — kiểm tra VK13)"} {l.Key.ConditionType} " +
                $"{l.ValidFrom:dd.MM.yyyy}–{l.ValidTo:dd.MM.yyyy} giá {l.Item.Amount:0.##}.",
                MessageV1: knumh, MessageV2: source?.ConditionRecordNumber,
                MessageV3: source?.Amount?.ToString("0.##"), MessageV4: l.Item.Amount.ToString("0.##"),
                Item: l.Index.ToString()));
        }
        return RowsResult(all, lines.Count, failed.Count, lines.Count == 1 ? single : null);
    }

    /// <summary>Tổng hợp kết quả lô ghi từng dòng: hết lỗi → Succeeded; lỗi hết → Failed; lỗi một phần → Failed PRICE_PARTIAL.</summary>
    private static SapCallResult RowsResult(List<SapReturnMessage> messages, int total, int failed, string? singleKnumh)
    {
        var ordered = messages.OrderBy(m => int.TryParse(m.Item, out var i) ? i : 0).ToList();
        if (failed == 0) return SapCallResult.Success(new SapDocumentRef(singleKnumh, ObjectType: "KONH"), ordered);
        if (failed == total)
            return SapCallResult.Failed(SapErrorCodes.Business,
                $"SAP không ghi được dòng nào ({total} dòng). " +
                string.Join(" | ", ordered.Where(m => m.IsError && m.Id != "RDM").Take(5).Select(m => $"[{m.Item}] {m.Message}")),
                ordered);
        return SapCallResult.Failed(PriceErrorCodes.Partial,
            $"SAP ghi {total - failed}/{total} dòng; {failed} dòng lỗi ({string.Join(", ", ordered.Where(m => m.Number is "PRICE_NOT_CREATED" or "PRICE_NOT_CHANGED").Select(m => m.Item))}). " +
            "Gửi lại an toàn: dòng đã ghi sẽ bị chặn ở bước kiểm tra (tạo) hoặc ghi lại cùng giá trị (sửa).",
            ordered);
    }

    private sealed record UploadCall(SapCallResult? Error, List<SapReturnMessage> Messages, HashSet<int> FailedRows);

    /// <summary>
    /// Gọi Z_ESD015_PRICE_UPLOAD. Hàm commit từng dòng (CALL TRANSACTION UPDATE 'S') → lỗi ABAP / mất kết nối giữa chừng là Unknown.
    /// Dòng lỗi = có message E/A với ROW = ROW_NO; ROW = 0 là lỗi chung (không dòng nào được xử lý).
    /// </summary>
    private UploadCall CallUpload(RfcDestination dest, CallContext ctx, string action, string? conditionType, IReadOnlyList<Row> rows)
    {
        IRfcFunction fn;
        try
        {
            fn = dest.Repository.CreateFunction(_options.PriceConditionFunction);
        }
        catch (RfcBaseException ex) when (ex is not (RfcCommunicationException or RfcLogonException or RfcResourceException))
        {
            return new UploadCall(SapCallResult.Failed(PriceErrorCodes.FunctionMissing,
                $"Hàm RFC {_options.PriceConditionFunction} chưa có trên SAP — tạo theo abap/Z_ESD015_PRICE_UPLOAD.md. Chi tiết: {ex.Message}"),
                new(), new());
        }

        if (conditionType is not null) fn.SetValue("IV_KSCHL", conditionType);
        fn.SetValue("IV_ACTION", action);
        AppendRows(fn.GetTable("IT_PRICE"), rows);

        var invokeError = Invoke(fn, dest, ctx, abapErrorMayFollowCommit: true);
        if (invokeError is not null) return new UploadCall(invokeError, new(), new());

        var messages = new List<SapReturnMessage>();
        foreach (IRfcStructure r in fn.GetTable("ET_RETURN"))
        {
            var m = ReadReturnRow(r);
            if (m is null) continue;
            var row = r.GetInt("ROW");
            messages.Add(m with { Item = row == 0 ? null : row.ToString() });
        }

        var general = messages.Where(m => m.IsError && m.Item is null).ToList();
        if (general.Count > 0)
            return new UploadCall(SapCallResult.Failed(SapErrorCodes.Business,
                string.Join("; ", general.Select(m => m.Message)), messages), messages, new());

        var failedRows = messages.Where(m => m.IsError && int.TryParse(m.Item, out _)).Select(m => int.Parse(m.Item!)).ToHashSet();
        return new UploadCall(null, messages, failedRows);
    }

    /// <summary>Đọc các kỳ hiệu lực (dòng Axxx) theo khóa (đủ hoặc một phần) + KONP; lọc theo khoảng hiệu lực và KFRST.</summary>
    private List<PriceRecord> ReadPriceRecords(RfcDestination dest, PriceKey key, DateOnly? from, DateOnly? to, string? releaseStatus)
    {
        var table = key.Table;
        var fields = new[] { "KNUMH", "KFRST", "KBSTAT", "DATAB", "DATBI" }.Concat(table.Fields.Select(f => f.Name)).ToArray();
        var where = new List<string>
        {
            RfcTableReader.Condition("KAPPL", PriceConditionRules.Application),
            RfcTableReader.Condition("KSCHL", key.ConditionType, and: true)
        };
        foreach (var f in table.Fields)
            if (key.Values.TryGetValue(f.Name, out var v) && v.Length > 0)
                where.Add(RfcTableReader.Condition(f.Name, v, and: true));
        if (to is not null) where.Add($"AND DATAB <= '{SapRequestMapper.Dats(to)}'");
        if (from is not null) where.Add($"AND DATBI >= '{SapRequestMapper.Dats(from)}'");
        if (releaseStatus is not null) where.Add(RfcTableReader.Condition("KFRST", releaseStatus, and: true));

        var rows = RfcTableReader.Read(dest, table.TableName, fields, where, _options.ReadTableBatchSize);
        var details = ReadConditionItems(dest, rows.Select(r => r["KNUMH"]).Distinct().ToList());

        return rows.Select(r =>
        {
            details.TryGetValue(r["KNUMH"], out var d);
            var values = table.Fields.ToDictionary(f => f.Name, f => r[f.Name], StringComparer.Ordinal);
            return new PriceRecord(r["KNUMH"], new PriceKey(key.ConditionType, table, values), r["KFRST"],
                RfcTableReader.NullIfEmpty(r["KBSTAT"]),
                RfcTableReader.ParseDate(r["DATAB"]) ?? DateOnly.MinValue, RfcTableReader.ParseDate(r["DATBI"]) ?? PriceConditionRules.MaxDate,
                d?.Deleted ?? false, d?.Amount, d?.Currency, d?.PricingUnit, d?.Unit, d?.CalculationType);
        }).ToList();
    }

    /// <summary>Tất cả kỳ hiệu lực của 1 KNUMH (KONH → bảng Axxx). null + lỗi nếu không có / không thuộc 4 loại.</summary>
    private List<PriceRecord>? ReadRecordByNumber(RfcDestination dest, string knumh, out (string Code, string Message)? error)
    {
        var found = ReadRecordsByNumbers(dest, new[] { knumh })[knumh];
        error = found.Error;
        return found.Periods;
    }

    /// <summary>Kết quả đọc 1 KNUMH: các kỳ hiệu lực (sắp theo ngày bắt đầu) hoặc lỗi (không có / không thuộc 4 loại).</summary>
    private sealed record RecordLookup(List<PriceRecord>? Periods, (string Code, string Message)? Error);

    /// <summary>
    /// Đọc nhiều KNUMH theo lô: KONH (50 số / lần) → Axxx theo (bảng, loại) → KONP. Thay cho đọc từng record
    /// (3 lần RFC / record) — dùng cho sửa / hủy / duyệt hàng loạt và xác nhận sau khi ghi.
    /// </summary>
    private Dictionary<string, RecordLookup> ReadRecordsByNumbers(RfcDestination dest, IEnumerable<string> numbers)
    {
        var list = numbers.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, RecordLookup>(StringComparer.Ordinal);

        var heads = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var chunk in list.Chunk(50))
            foreach (var h in RfcTableReader.Read(dest, "KONH", new[] { "KNUMH", "KAPPL", "KSCHL", "KOTABNR", "KVEWE" },
                         RfcTableReader.AnyOf("KNUMH", chunk), _options.ReadTableBatchSize))
                heads[h["KNUMH"]] = h;

        var supported = new List<(string Knumh, string Kschl, PriceTableDef Table)>();
        foreach (var knumh in list)
        {
            if (!heads.TryGetValue(knumh, out var head))
            {
                result[knumh] = new(null, (PriceErrorCodes.NotFound, $"Không tìm thấy bảng giá {knumh}."));
                continue;
            }
            var table = PriceConditionRules.TableByNumber(head["KOTABNR"]);
            if (head["KVEWE"] != PriceConditionRules.Usage || head["KAPPL"] != PriceConditionRules.Application ||
                !PriceConditionRules.ConditionTypes.Contains(head["KSCHL"]) || table is null)
            {
                result[knumh] = new(null, (PriceErrorCodes.NotSupported,
                    $"Bảng giá {knumh} ({head["KSCHL"]}/A{head["KOTABNR"]}) không thuộc ZVLP / ZVDI / ZMIN / ZVIV."));
                continue;
            }
            supported.Add((knumh, head["KSCHL"], table));
        }

        var rowsByKnumh = new Dictionary<string, List<(Dictionary<string, string> Row, string Kschl, PriceTableDef Table)>>(StringComparer.Ordinal);
        foreach (var group in supported.GroupBy(s => (s.Kschl, s.Table.Number)))
        {
            var table = group.First().Table;
            var fields = new[] { "KNUMH", "KFRST", "KBSTAT", "DATAB", "DATBI" }.Concat(table.Fields.Select(f => f.Name)).ToArray();
            foreach (var chunk in group.Select(g => g.Knumh).Chunk(50))
            {
                var where = new List<string>
                {
                    RfcTableReader.Condition("KAPPL", PriceConditionRules.Application),
                    RfcTableReader.Condition("KSCHL", group.Key.Kschl, and: true)
                };
                RfcTableReader.AndAnyOf(where, "KNUMH", chunk);
                foreach (var r in RfcTableReader.Read(dest, table.TableName, fields, where, _options.ReadTableBatchSize))
                {
                    if (!rowsByKnumh.TryGetValue(r["KNUMH"], out var rows)) rowsByKnumh[r["KNUMH"]] = rows = new();
                    rows.Add((r, group.Key.Kschl, table));
                }
            }
        }

        var details = ReadConditionItems(dest, rowsByKnumh.Keys.ToList());
        foreach (var (knumh, _, table) in supported)
        {
            if (!rowsByKnumh.TryGetValue(knumh, out var rows))
            {
                result[knumh] = new(null, (PriceErrorCodes.NotFound, $"Bảng giá {knumh} không còn dòng hiệu lực nào trong A{table.Number}."));
                continue;
            }
            details.TryGetValue(knumh, out var d);
            result[knumh] = new(rows.Select(x => ToPriceRecord(x.Row, x.Kschl, x.Table, d)).OrderBy(p => p.ValidFrom).ToList(), null);
        }
        return result;
    }

    private static PriceRecord ToPriceRecord(Dictionary<string, string> r, string kschl, PriceTableDef table, ConditionItem? d)
    {
        var values = table.Fields.ToDictionary(f => f.Name, f => r[f.Name], StringComparer.Ordinal);
        return new PriceRecord(r["KNUMH"], new PriceKey(kschl, table, values), r["KFRST"], RfcTableReader.NullIfEmpty(r["KBSTAT"]),
            RfcTableReader.ParseDate(r["DATAB"]) ?? DateOnly.MinValue, RfcTableReader.ParseDate(r["DATBI"]) ?? PriceConditionRules.MaxDate,
            d?.Deleted ?? false, d?.Amount, d?.Currency, d?.PricingUnit, d?.Unit, d?.CalculationType);
    }

    /// <summary>
    /// Đọc bảng giá có sẵn của NHIỀU khóa trong khoảng [from, to] theo lô: mỗi (loại, bảng) đọc Axxx theo sales org +
    /// danh sách vật tư (30 mã / lần), lọc khóa trong bộ nhớ, rồi đọc KONP cho các record khớp.
    /// Thay cho 1 lần đọc / khóa khi kiểm tra chồng hiệu lực của upload hàng loạt. Trả theo PriceKey.Identity.
    /// </summary>
    private Dictionary<string, List<PriceRecord>> ReadPriceRecordsBulk(RfcDestination dest, IEnumerable<PriceKey> keys, DateOnly from, DateOnly to)
    {
        var byIdentity = new Dictionary<string, List<PriceRecord>>(StringComparer.Ordinal);
        var wanted = keys.GroupBy(k => k.Identity).Select(g => g.First()).ToList();
        var hits = new List<(Dictionary<string, string> Row, string Kschl, PriceTableDef Table, string Identity)>();

        foreach (var group in wanted.GroupBy(k => (k.ConditionType, k.Table.Number)))
        {
            var table = group.First().Table;
            var identities = group.Select(k => k.Identity).ToHashSet(StringComparer.Ordinal);
            var orgField = table.Fields.Any(f => f.Name == "VKORG") ? "VKORG" : "VKORGAU";
            var orgs = group.Select(k => k.Values.GetValueOrDefault(orgField) ?? string.Empty).Distinct().ToList();
            var fields = new[] { "KNUMH", "KFRST", "KBSTAT", "DATAB", "DATBI" }.Concat(table.Fields.Select(f => f.Name)).ToArray();

            foreach (var materials in group.Select(k => k.Values.GetValueOrDefault("MATNR") ?? string.Empty).Distinct().Chunk(30))
            {
                var where = new List<string>
                {
                    RfcTableReader.Condition("KAPPL", PriceConditionRules.Application),
                    RfcTableReader.Condition("KSCHL", group.Key.ConditionType, and: true),
                    $"AND DATAB <= '{SapRequestMapper.Dats(to)}'",
                    $"AND DATBI >= '{SapRequestMapper.Dats(from)}'"
                };
                RfcTableReader.AndAnyOf(where, "MATNR", materials);
                if (orgs.Count <= 20) RfcTableReader.AndAnyOf(where, orgField, orgs);
                foreach (var r in RfcTableReader.Read(dest, table.TableName, fields, where, _options.ReadTableBatchSize))
                {
                    var values = table.Fields.ToDictionary(f => f.Name, f => r[f.Name], StringComparer.Ordinal);
                    var identity = new PriceKey(group.Key.ConditionType, table, values).Identity;
                    if (identities.Contains(identity)) hits.Add((r, group.Key.ConditionType, table, identity));
                }
            }
        }

        var details = ReadConditionItems(dest, hits.Select(h => h.Row["KNUMH"]).Distinct().ToList());
        foreach (var (row, kschl, table, identity) in hits)
        {
            details.TryGetValue(row["KNUMH"], out var d);
            if (!byIdentity.TryGetValue(identity, out var list)) byIdentity[identity] = list = new();
            list.Add(ToPriceRecord(row, kschl, table, d));
        }
        return byIdentity;
    }

    private sealed record ConditionItem(decimal Amount, string Currency, int PricingUnit, string Unit, bool Deleted, string? CalculationType);

    /// <summary>KONP dòng 01: giá (quy đổi từ dạng lưu nội bộ theo TCURX), tiền tệ, pricing unit, đơn vị, cờ xóa, calculation type.</summary>
    private Dictionary<string, ConditionItem> ReadConditionItems(RfcDestination dest, IReadOnlyList<string> numbers)
    {
        var result = new Dictionary<string, ConditionItem>(StringComparer.Ordinal);
        foreach (var chunk in numbers.Chunk(50))
        {
            var where = RfcTableReader.AnyOf("KNUMH", chunk).ToList();
            where.Add(RfcTableReader.Condition("KOPOS", "01", and: true));
            foreach (var r in RfcTableReader.Read(dest, "KONP", new[] { "KNUMH", "KBETR", "KONWA", "KPEIN", "KMEIN", "LOEVM_KO", "KRECH" },
                         where, _options.ReadTableBatchSize))
            {
                var currency = r["KONWA"];
                var amount = RfcTableReader.ToExternalAmount(RfcTableReader.ParseDecimal(r["KBETR"]), CurrencyDecimals(dest, currency));
                result[r["KNUMH"]] = new ConditionItem(amount, currency,
                    int.TryParse(r["KPEIN"], out var per) ? per : 1, r["KMEIN"], r["LOEVM_KO"].Length > 0,
                    RfcTableReader.NullIfEmpty(r["KRECH"]));
            }
        }
        return result;
    }

    private int CurrencyDecimals(RfcDestination dest, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return 2;
        return _currencyDecimals.GetOrAdd(currency, c =>
        {
            var row = RfcTableReader.Read(dest, "TCURX", new[] { "CURRKEY", "CURRDEC" },
                new[] { RfcTableReader.Condition("CURRKEY", c) }, 1).FirstOrDefault();
            return row is not null && int.TryParse(row["CURRDEC"], out var dec) ? dec : 2;
        });
    }

    private static PriceKeyInput ToKeyInput(PriceQuery q) => new()
    {
        ConditionType = q.ConditionType, SalesOrganization = q.SalesOrganization, DistributionChannel = q.DistributionChannel,
        SoldTo = q.SoldTo, ShipTo = q.ShipTo, Material = q.Material, ShippingCondition = q.ShippingCondition,
        Plant = q.Plant, Incoterms = q.Incoterms, Region = q.Region
    };

    internal static PriceRecordDto ToDto(PriceRecord r)
    {
        string? K(string field) => r.Key.Values.TryGetValue(field, out var v) && v.Length > 0 ? v : null;
        var material = K("MATNR");
        return new PriceRecordDto(
            r.ConditionRecordNumber, r.Key.ConditionType, r.Key.Table.TableName, r.Key.Values, r.ReleaseStatus, r.Pending,
            r.ValidFrom, r.ValidTo, r.Deleted, r.Amount, r.Currency, r.PricingUnit, r.Unit)
        {
            SalesOrganization = K("VKORG") ?? K("VKORGAU"),
            DistributionChannel = K("VTWEG"),
            SoldTo = K("KUNAG"),
            ShipTo = K("KUNWE"),
            Material = material,
            MaterialCode = material is null ? null : SapMasterDataClient.ExternalCode(material),
            ShippingCondition = K("VSBED"),
            Plant = K("WERKS"),
            Incoterms = K("INCO1"),
            Region = K("REGIO"),
            ProcessingStatus = r.ProcessingStatus,
            CalculationType = r.CalculationType
        };
    }

    private static string AmountRule(string conditionType) =>
        conditionType == "ZVDI" ? "ZVDI (chiết khấu) phải là số âm." : $"{conditionType} phải là số dương.";

    private static SapReturnMessage ItemMessage(string type, string code, int index, string text, string? v1 = null) =>
        new(type, "RDM", code, text, MessageV1: v1, Item: index == 0 ? null : index.ToString());

    private static SapReturnMessage KnumhMessage(string type, string code, string knumh, string text) =>
        new(type, "RDM", code, text, MessageV1: knumh, Item: knumh);

    private static SapReturnMessage WithSource(SapReturnMessage m, IReadOnlyDictionary<int, PriceRecord> sources) =>
        int.TryParse(m.Item, out var i) && sources.TryGetValue(i, out var s) ? m with { MessageV2 = s.ConditionRecordNumber } : m;

    private static SapCallResult ValidationFailed(List<SapReturnMessage> messages)
    {
        messages = messages.OrderBy(m => int.TryParse(m.Item, out var i) ? i : 0).ToList();
        var errors = messages.Where(m => m.IsError).ToList();
        var lines = errors.Select(e => e.Item).Distinct().Count();
        return SapCallResult.Failed(PriceErrorCodes.Validation,
            $"{lines} dòng không hợp lệ — không ghi SAP. " + string.Join(" | ", errors.Take(5).Select(e => $"[{e.Item}] {e.Number}: {e.Message}")),
            messages);
    }
}
