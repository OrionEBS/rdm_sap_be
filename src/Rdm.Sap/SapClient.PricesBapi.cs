using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Rdm.Application.Postings;
using Rdm.Application.Requests;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Bảng giá ZVLP / ZVDI / ZMIN / ZVIV qua BAPI_PRICES_CONDITIONS — gọi qua vỏ RFC Z_ESD016_PRICES_CONDITIONS
/// (Sap:PriceBapiFunction, source abap/Z_ESD016_PRICES_CONDITIONS.abap) vì BAPI không remote-enabled trên DRF-930.
/// Tách riêng với phương án BDC (Z_ESD015_PRICE_UPLOAD, /api/sd/prices) để so sánh.
///
/// BAPI KHÔNG tự kiểm tra master data / chồng hiệu lực → kiểm tra TUẦN TỰ theo nguyên tắc thận trọng, bước nào có lỗi thì dừng,
/// không chạy bước sau và không ghi SAP:
///   1. Đầu vào (loại điều kiện, khóa, hiệu lực, dấu giá; sửa / đổi trạng thái: record tồn tại, chưa xóa)
///   2. Master data tồn tại trên SAP
///   3. Chồng hiệu lực với bảng giá có sẵn
///   4. Trùng lắp giữa các dòng trong request
/// Mỗi bước bọc try/catch: lỗi kỹ thuật → PRICE_VALIDATION_ERROR (mất kết nối → SAP_UNAVAILABLE), message chỉ có tên bước +
/// mã tham chiếu (chi tiết exception ghi log). Vỏ RFC kiểm tra lại lần nữa trên SAP (quyền, master data, chồng hiệu lực).
///
/// Mỗi condition record = 1 lần gọi (BAPI + commit trong vỏ RFC) → kết quả từng record độc lập, đếm được số record thành công.
/// Message của từng record: Item = số dòng (tạo / sửa) hoặc KNUMH (hủy / duyệt / gỡ duyệt);
///   message SAP giữ nguyên Id / Number (mã lỗi SAP); message kết luận Id = RDM, Number = PRICE_OK / mã lỗi, MessageV1 = KNUMH.
/// </summary>
public sealed partial class SapClient
{
    private readonly ConcurrentDictionary<string, string> _calculationTypes = new(StringComparer.Ordinal);

    private const string StageInput = "dữ liệu đầu vào";
    private const string StageMasterData = "master data";
    private const string StageOverlap = "chồng hiệu lực với bảng giá có sẵn";
    private const string StageDuplicate = "trùng lắp trong request";
    private const string StagePrepare = "chuẩn bị dữ liệu gửi SAP";

    // ------------------------------------------------------------------ tạo

    public Task<SapCallResult> BapiCreatePricesAsync(PriceBapiCreateRequest request, CancellationToken ct) =>
        RunAsync(_options.PriceBapiFunction, stateful: false, (dest, ctx) =>
        {
            var messages = new List<SapReturnMessage>();
            var lines = new List<PriceCreateLine>();
            var calls = new List<BapiRecordCall>();
            BulkProgress.Current?.SetTotal(request.Items.Count);

            var stop =
                Stage(StageInput, messages, () => lines = ValidateCreateInput(request.Items, messages))
                ?? Stage(StageMasterData, messages, () => messages.AddRange(ValidateMasterData(dest,
                    lines.Select(l => (l.Index, (IReadOnlyDictionary<string, string>)l.Key.Values,
                        SapRequestMapper.Code(l.Item.Unit), SapRequestMapper.Code(l.Item.Currency) ?? "VND")))))
                ?? Stage(StageOverlap, messages, () => CheckOverlapWithExisting(dest, lines, PriceOverlapMode.Reject, messages))
                ?? Stage(StageDuplicate, messages, () => AddWithinRequestErrors(lines.Select(l => (l.Index, l.Key, l.ValidFrom, l.ValidTo)), messages))
                ?? BapiFunctionMissing(dest)
                ?? Stage(StagePrepare, messages, () =>
                {
                    var today = DateOnly.FromDateTime(DateTime.Today);
                    var createdBy = _options.User.Trim().ToUpperInvariant();
                    calls.AddRange(lines.Select(l => new BapiRecordCall(
                        l.Index.ToString(),
                        null,
                        PriceBapiMapper.Create(l.Key, l.ValidFrom, l.ValidTo,
                            new PriceBapiItemValues(l.Item.Amount, SapRequestMapper.Code(l.Item.Currency) ?? "VND", l.Item.PricingUnit,
                                SapRequestMapper.Code(l.Item.Unit)!, CalculationType(dest, l.Key.ConditionType),
                                request.PendingApproval ? "A" : string.Empty, Delete: false),
                            createdBy, today),
                        knumh => $"Tạo bảng giá {knumh} {l.Key.ConditionType}/A{l.Key.Table.Number} {l.ValidFrom:dd.MM.yyyy}–{l.ValidTo:dd.MM.yyyy} " +
                                 $"giá {l.Item.Amount:0.##}{(request.PendingApproval ? " (chờ duyệt)" : "")}.",
                        Verify: null)));
                });

            return stop ?? ExecuteBapiCalls(dest, ctx, calls, messages, idempotent: false);
        }, ct);

    // ------------------------------------------------------------------ sửa

    /// <summary>1 dòng sửa đã qua bước kiểm tra đầu vào.</summary>
    private sealed record BapiChangeLine(
        int Index, string Knumh, PriceChangeItem Item, List<PriceRecord> Periods, PriceRecord Current, PriceRecord Period);

    public Task<SapCallResult> BapiChangePricesAsync(PriceBapiChangeRequest request, CancellationToken ct) =>
        RunAsync(_options.PriceBapiFunction, stateful: false, (dest, ctx) =>
        {
            var messages = new List<SapReturnMessage>();
            var lines = new List<BapiChangeLine>();
            var calls = new List<BapiRecordCall>();
            BulkProgress.Current?.SetTotal(request.Items.Count);

            var stop =
                Stage(StageInput, messages, () => lines = ValidateChangeInput(dest, request.Items, messages))
                ?? Stage(StageMasterData, messages, () => messages.AddRange(ValidateMasterData(dest, lines
                    .Where(l => SapRequestMapper.Code(l.Item.Unit) is not null)
                    .Select(l => (l.Index, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(),
                        SapRequestMapper.Code(l.Item.Unit), (string?)null)))))
                ?? Stage(StageOverlap, messages, () =>
                {
                    // Kéo dài hiệu lực đến → kiểm tra chồng với bảng giá khác cùng khóa trong phần kéo dài (luôn chặn).
                    var extended = lines.Where(l => l.Item.ValidTo is { } to && to > l.Period.ValidTo).ToList();
                    if (extended.Count == 0) return;
                    var existing = ReadPriceRecordsBulk(dest, extended.Select(l => l.Current.Key),
                        extended.Min(l => l.Period.ValidTo.AddDays(1)), extended.Max(l => l.Item.ValidTo!.Value));
                    foreach (var l in extended)
                    {
                        var (extFrom, newTo) = (l.Period.ValidTo.AddDays(1), l.Item.ValidTo!.Value);
                        var others = (existing.GetValueOrDefault(l.Current.Key.Identity) ?? new List<PriceRecord>())
                            .Where(r => PriceConditionRules.Overlaps(extFrom, newTo, r.ValidFrom, r.ValidTo));
                        foreach (var (type, code, text) in PriceConditionRules.CheckAgainstExisting(
                                     l.Current.Key, l.Period.ValidFrom, newTo, others, PriceOverlapMode.Reject, ignoreRecord: l.Knumh))
                            messages.Add(ItemMessage(type, code, l.Index, text));
                    }
                })
                ?? Stage(StageDuplicate, messages, () => AddWithinRequestErrors(lines
                    .Where(l => l.Item.ValidTo is { } to && to > l.Period.ValidTo)
                    .Select(l => (l.Index, l.Current.Key, l.Period.ValidFrom, l.Item.ValidTo!.Value)), messages))
                ?? BapiFunctionMissing(dest)
                ?? Stage(StagePrepare, messages, () => calls.AddRange(lines.Select(l => ChangeCall(dest, l))));

            return stop ?? ExecuteBapiCalls(dest, ctx, calls, messages, idempotent: false);
        }, ct);

    /// <summary>Bước 1 của sửa: KNUMH không lặp, có field sửa, record tồn tại / chưa xóa, kỳ hiệu lực, dấu giá.</summary>
    private List<BapiChangeLine> ValidateChangeInput(RfcDestination dest, IReadOnlyList<PriceChangeItem> items, List<SapReturnMessage> messages)
    {
        var lines = new List<BapiChangeLine>();
        var seen = new HashSet<string>();
        // Đọc 1 lần cho mọi KNUMH (theo lô) thay vì từng record.
        var found = ReadRecordsByNumbers(dest, items.Select(i => AccDocumentMapper.Alpha(i.ConditionRecordNumber, 10)!));
        for (var i = 0; i < items.Count; i++)
        {
            var index = i + 1;
            var item = items[i];
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

            var periods = found[knumh].Periods;
            if (periods is null)
            {
                var notFound = found[knumh].Error!.Value;
                messages.Add(ItemMessage("E", notFound.Code, index, notFound.Message));
                continue;
            }
            var current = periods[0];
            if (current.Deleted)
            {
                messages.Add(ItemMessage("E", PriceErrorCodes.Deleted, index, $"Bảng giá {knumh} đã bị đánh dấu xóa."));
                continue;
            }

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
            lines.Add(new BapiChangeLine(index, knumh, item, periods, current, period));
        }
        return lines;
    }

    private BapiRecordCall ChangeCall(RfcDestination dest, BapiChangeLine l)
    {
        var (item, current, knumh) = (l.Item, l.Current, l.Knumh);
        var changedFrom = l.Period.ValidFrom;
        var changedTo = l.Period.ValidTo;
        var newPeriods = l.Periods
            .Select(p => (From: p.ValidFrom, To: p.ValidTo,
                NewTo: p.ValidFrom == changedFrom && p.ValidTo == changedTo && item.ValidTo is { } t ? t : p.ValidTo))
            .ToList();
        var values = CurrentValues(dest, current) with
        {
            Amount = item.Amount ?? current.Amount ?? 0m,
            PricingUnit = item.PricingUnit ?? current.PricingUnit ?? 1,
            Unit = SapRequestMapper.Code(item.Unit) ?? current.Unit ?? string.Empty
        };
        var changes = string.Join(", ", new[]
        {
            item.Amount is null ? null : $"giá {current.Amount:0.##} → {item.Amount:0.##}",
            item.PricingUnit is null ? null : $"pricing unit {current.PricingUnit} → {item.PricingUnit}",
            item.Unit is null ? null : $"đơn vị {current.Unit} → {values.Unit}",
            item.ValidTo is null ? null : $"hiệu lực đến {changedTo:dd.MM.yyyy} → {item.ValidTo:dd.MM.yyyy}"
        }.Where(x => x is not null));
        // Đổi hiệu lực đến: đọc lại để chắc kỳ cũ đã bị thay (không còn 2 dòng chồng nhau).
        Func<List<PriceRecord>?, bool>? verify = item.ValidTo is { } vt && vt != changedTo
            ? after => after is not null
                       && after.Any(p => p.ValidFrom == changedFrom && p.ValidTo == vt)
                       && !after.Any(p => p.ValidFrom == changedFrom && p.ValidTo == changedTo)
            : null;
        return new BapiRecordCall(l.Index.ToString(), knumh,
            PriceBapiMapper.Change(knumh, current.Key, newPeriods, values),
            _ => $"Đã sửa bảng giá {knumh}: {changes}.", verify);
    }

    // ------------------------------------------------------------------ hủy hiệu lực / duyệt / gỡ duyệt

    public Task<SapCallResult> BapiInvalidatePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) =>
        StatusChangeAsync(request, StatusAction.Invalidate, ct);

    public Task<SapCallResult> BapiApprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) =>
        StatusChangeAsync(request, StatusAction.Approve, ct);

    public Task<SapCallResult> BapiUnapprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) =>
        StatusChangeAsync(request, StatusAction.Unapprove, ct);

    private enum StatusAction { Invalidate, Approve, Unapprove }

    /// <summary>
    /// Hủy hiệu lực / duyệt / gỡ duyệt: idempotent — record đã ở trạng thái đích được bỏ qua; xác nhận lại trên SAP sau khi ghi.
    /// Duyệt: bản chờ duyệt chồng hiệu lực với bản ĐÃ duyệt cùng khóa → chặn (thận trọng: không để 2 giá hiệu lực cùng lúc).
    /// </summary>
    private Task<SapCallResult> StatusChangeAsync(PriceBapiRecordsRequest request, StatusAction action, CancellationToken ct) =>
        RunAsync(_options.PriceBapiFunction, stateful: false, (dest, ctx) =>
        {
            var messages = new List<SapReturnMessage>();
            var records = new List<(string Knumh, List<PriceRecord> Periods)>();
            var calls = new List<BapiRecordCall>();
            var numbers = request.ConditionRecordNumbers.Select(n => AccDocumentMapper.Alpha(n, 10)!).Distinct().ToList();
            var progress = BulkProgress.Current;
            progress?.SetTotal(numbers.Count);

            var stop =
                Stage(StageInput, messages, () =>
                {
                    var found = ReadRecordsByNumbers(dest, numbers);   // theo lô
                    foreach (var knumh in numbers)
                        if (CheckStatusInput(knumh, found[knumh], action, messages) is { } periods)
                            records.Add((knumh, periods));
                    progress?.Skip(messages.Count(m => m.Type == "I"));   // đã ở trạng thái đích
                })
                ?? Stage(StageOverlap, messages, () =>
                {
                    // Duyệt: bản chờ duyệt không được chồng hiệu lực với bản ĐÃ duyệt cùng khóa.
                    if (action != StatusAction.Approve || records.Count == 0) return;
                    var pending = records.SelectMany(r => r.Periods.Where(p => p.Pending).Select(p => (r.Knumh, Period: p))).ToList();
                    if (pending.Count == 0) return;
                    var existing = ReadPriceRecordsBulk(dest, pending.Select(x => x.Period.Key),
                        pending.Min(x => x.Period.ValidFrom), pending.Max(x => x.Period.ValidTo));
                    foreach (var (knumh, p) in pending)
                    {
                        var approved = (existing.GetValueOrDefault(p.Key.Identity) ?? new List<PriceRecord>()).Where(r => !r.Pending);
                        foreach (var (type, code, text) in PriceConditionRules.CheckAgainstExisting(
                                     p.Key, p.ValidFrom, p.ValidTo, approved, PriceOverlapMode.Reject, knumh))
                            messages.Add(KnumhMessage(type, code, knumh, text));
                    }
                })
                ?? (records.Count == 0 ? SapCallResult.Success(new SapDocumentRef(null, ObjectType: "KONH"), messages) : null)
                ?? BapiFunctionMissing(dest)
                ?? Stage(StagePrepare, messages, () => calls.AddRange(records.Select(r => StatusCall(dest, r.Knumh, r.Periods, action))));

            return stop ?? ExecuteBapiCalls(dest, ctx, calls, messages, idempotent: true);
        }, ct);

    /// <summary>Record tồn tại, chưa xóa, chưa ở trạng thái đích. Trả null nếu lỗi hoặc bỏ qua.</summary>
    private static List<PriceRecord>? CheckStatusInput(string knumh, RecordLookup found, StatusAction action, List<SapReturnMessage> messages)
    {
        var periods = found.Periods;
        if (periods is null)
        {
            messages.Add(KnumhMessage("E", found.Error!.Value.Code, knumh, found.Error.Value.Message));
            return null;
        }
        if (periods[0].Deleted)
        {
            messages.Add(action == StatusAction.Invalidate
                ? KnumhMessage("I", PriceErrorCodes.AlreadyDeleted, knumh, $"Bảng giá {knumh} đã hủy hiệu lực — bỏ qua.")
                : KnumhMessage("E", PriceErrorCodes.Deleted, knumh, $"Bảng giá {knumh} đã bị đánh dấu xóa."));
            return null;
        }
        if (action == StatusAction.Approve && !periods.Any(p => p.Pending))
        {
            messages.Add(KnumhMessage("I", PriceErrorCodes.AlreadyApproved, knumh, $"Bảng giá {knumh} đã duyệt — bỏ qua."));
            return null;
        }
        if (action == StatusAction.Unapprove && periods.All(p => p.Pending))
        {
            messages.Add(KnumhMessage("I", PriceErrorCodes.AlreadyPending, knumh, $"Bảng giá {knumh} đang chờ duyệt — bỏ qua."));
            return null;
        }
        return periods;
    }

    private BapiRecordCall StatusCall(RfcDestination dest, string knumh, List<PriceRecord> periods, StatusAction action)
    {
        var current = periods[0];
        var values = CurrentValues(dest, current) with
        {
            ReleaseStatus = action switch
            {
                StatusAction.Approve => string.Empty,
                StatusAction.Unapprove => "A",
                _ => current.ReleaseStatus
            },
            Delete = action == StatusAction.Invalidate
        };
        var (doneText, verify) = action switch
        {
            StatusAction.Approve => ($"Bảng giá {knumh} đã duyệt.", (Func<List<PriceRecord>, bool>)(r => r.All(p => !p.Pending))),
            StatusAction.Unapprove => ($"Bảng giá {knumh} đã gỡ duyệt (chờ duyệt).", r => r.All(p => p.Pending)),
            _ => ($"Bảng giá {knumh} đã hủy hiệu lực (đánh dấu xóa).", r => r.All(p => p.Deleted))
        };
        return new BapiRecordCall(knumh, knumh,
            PriceBapiMapper.Change(knumh, current.Key, periods.Select(p => (p.ValidFrom, p.ValidTo, p.ValidTo)).ToList(), values),
            _ => doneText,
            Verify: after => after is not null && verify(after));
    }

    // ------------------------------------------------------------------ kiểm tra tuần tự

    /// <summary>
    /// Chạy 1 bước kiểm tra. Trả null → qua bước, chạy tiếp; khác null → dừng, không ghi SAP:
    /// có dòng lỗi → PRICE_VALIDATION (422); exception → mã lỗi + message an toàn (tên bước + mã tham chiếu log).
    /// </summary>
    private SapCallResult? Stage(string stage, List<SapReturnMessage> messages, Action check)
    {
        try
        {
            check();
        }
        catch (Exception ex)
        {
            var reference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var (code, reason) = ex switch
            {
                RfcCommunicationException or RfcResourceException or BrokenCircuitException => (SapErrorCodes.Unavailable, "mất kết nối SAP"),
                RfcLogonException => (SapErrorCodes.Logon, "đăng nhập SAP thất bại"),
                RfcTypeConversionException => (PriceErrorCodes.ValidationError, "dữ liệu không chuyển được sang kiểu field SAP"),
                _ => (PriceErrorCodes.ValidationError, "lỗi kỹ thuật")
            };
            _logger.LogError(ex, "{BapiName}: lỗi ở bước kiểm tra '{Stage}' (ref {Reference}) — chưa ghi SAP",
                _options.PriceBapiFunction, stage, reference);
            return SapCallResult.Failed(code,
                $"Không kiểm tra được {stage} ({reason}) — chưa ghi SAP, gửi lại an toàn. Mã tham chiếu log: {reference}.", messages);
        }
        return messages.Any(m => m.IsError) ? ValidationFailed(messages) : null;
    }

    private static void AddWithinRequestErrors(IEnumerable<(int Index, PriceKey Key, DateOnly From, DateOnly To)> lines, List<SapReturnMessage> messages)
    {
        foreach (var (index, text) in PriceConditionRules.CheckWithinRequest(lines.ToList()))
            messages.Add(ItemMessage("E", PriceErrorCodes.DuplicateInRequest, index, text));
    }

    // ------------------------------------------------------------------ helpers


    /// <param name="Item">Số dòng (tạo / sửa) hoặc KNUMH (đổi trạng thái).</param>
    /// <param name="Knumh">KNUMH đã biết (sửa / đổi trạng thái); tạo mới = null, lấy từ TO_BAPIKNUMHS.</param>
    /// <param name="Verify">Kiểm tra trạng thái SAU khi ghi trên các kỳ đọc lại của record (null = không đọc được).</param>
    private sealed record BapiRecordCall(
        string Item, string? Knumh, PriceBapiTables Tables, Func<string?, string> SuccessText, Func<List<PriceRecord>?, bool>? Verify);

    private enum CallState { Ok, Failed, Lost }
    private enum BatchOutcome { Committed, Rejected, Lost }

    /// <summary>
    /// Ghi theo lô: tối đa Sap:PriceBapiChunkSize record / 1 lần gọi vỏ RFC (1 commit), cùng cờ PI_PHYSICAL_DELETION.
    ///   - Lô commit → xác nhận theo lô (1 lần đọc lại cho cả lô) → thành công từng record.
    ///   - Lô bị từ chối (SAP E/A, chưa commit — vỏ RFC đã rollback cả lô) → gọi lại TỪNG record của lô để biết đúng record lỗi;
    ///     record đã ghi thật (hiếm: lỗi lúc commit) bị vỏ RFC chặn ở bước chồng hiệu lực → không tạo trùng.
    ///   - Mất kết nối / lỗi không rõ khi đang gọi → cả lô không rõ đã ghi chưa, dừng các lô sau;
    ///     tạo / sửa trả Unknown (không gửi lại khi chưa kiểm tra); hủy / duyệt / gỡ duyệt idempotent → Failed (gửi lại an toàn).
    /// Lỗi kiểm tra của vỏ RFC (ID = ZRDM) → mã lỗi lấy từ PARAMETER (PRICE_OVERLAP, PRICE_INVALID_MASTER_DATA...).
    /// </summary>
    private SapCallResult ExecuteBapiCalls(
        RfcDestination dest, CallContext ctx, IReadOnlyList<BapiRecordCall> calls, List<SapReturnMessage> messages, bool idempotent)
    {
        var progress = BulkProgress.Current;
        int ok = 0, failed = 0;
        var connectionLost = false;
        string? single = null;

        void Done(bool success, string? knumh)
        {
            if (success)
            {
                ok++;
                single ??= knumh;
            }
            else failed++;
            progress?.Add(success ? 1 : 0, success ? 0 : 1);
        }

        void NotProcessed(BapiRecordCall call)
        {
            messages.Add(Final("E", PriceErrorCodes.NotProcessed, call, "Chưa xử lý do lỗi kết nối SAP ở lô trước.", call.Knumh));
            Done(false, null);
        }

        var chunkSize = Math.Max(1, _options.PriceBapiChunkSize);
        foreach (var batch in calls.GroupBy(c => c.Tables.PhysicalDeletion).SelectMany(g => g.Chunk(chunkSize)))
        {
            if (connectionLost)
            {
                foreach (var call in batch) NotProcessed(call);
                continue;
            }

            if (batch.Length > 1)
            {
                var outcome = InvokeBatch(dest, ctx, batch, out var numbers, out var lostRef);
                if (outcome == BatchOutcome.Committed)
                {
                    ConfirmBatch(dest, batch, numbers, messages, Done);
                    continue;
                }
                if (outcome == BatchOutcome.Lost)
                {
                    connectionLost = true;
                    foreach (var call in batch)
                    {
                        messages.Add(Final("E", SapErrorCodes.UnknownOutcome, call,
                            $"Mất kết nối / lỗi không rõ khi đang ghi lô {batch.Length} bảng giá — " +
                            $"{(idempotent ? "gửi lại an toàn" : "kiểm tra VK13 trước khi gửi lại")}. Mã tham chiếu log: {lostRef}.", call.Knumh));
                        Done(false, null);
                    }
                    continue;
                }
                // Rejected → ghi lại từng record để biết record nào lỗi.
            }

            foreach (var call in batch)
            {
                if (connectionLost)
                {
                    NotProcessed(call);
                    continue;
                }
                var (state, knumh) = InvokeSingle(dest, ctx, call, messages, idempotent);
                if (state == CallState.Lost) connectionLost = true;
                Done(state == CallState.Ok, knumh);
            }
        }

        var ordered = messages.OrderBy(m => int.TryParse(m.Item, out var i) ? i : 0).ToList();
        var summary = $"{ok}/{calls.Count} bảng giá thành công, {failed} lỗi.";
        if (failed == 0) return SapCallResult.Success(new SapDocumentRef(calls.Count == 1 ? single : null, ObjectType: "KONH"), ordered);
        if (connectionLost && !idempotent) return SapCallResult.Unknown(summary + " Lỗi kết nối giữa chừng.", ordered);
        if (connectionLost) return SapCallResult.Failed(SapErrorCodes.Unavailable, summary + " Lỗi kết nối giữa chừng — gửi lại an toàn.", ordered);
        return SapCallResult.Failed(ok == 0 ? SapErrorCodes.Business : PriceErrorCodes.Partial, summary, ordered);
    }

    /// <summary>
    /// 1 lần gọi vỏ RFC cho cả lô. Bảng giá tạo mới được đánh số tạm riêng ($000000001…) để map lại KNUMH thật qua TO_BAPIKNUMHS.
    /// </summary>
    private BatchOutcome InvokeBatch(RfcDestination dest, CallContext ctx, BapiRecordCall[] batch,
        out Dictionary<BapiRecordCall, string?> numbers, out string? lostReference)
    {
        numbers = new Dictionary<BapiRecordCall, string?>(ReferenceEqualityComparer.Instance);
        lostReference = null;
        var temporary = new Dictionary<string, BapiRecordCall>(StringComparer.Ordinal);

        IRfcFunction fn;
        try
        {
            fn = dest.Repository.CreateFunction(_options.PriceBapiFunction);
            var n = 0;
            foreach (var call in batch)
            {
                var tables = call.Tables;
                if (call.Knumh is null)
                {
                    var temp = "$" + (++n).ToString("D9");
                    tables = PriceBapiMapper.WithTemporaryNumber(tables, temp);
                    temporary[temp] = call;
                }
                AppendRows(fn.GetTable("TI_BAPICONDCT"), tables.ConditionTables);
                AppendRows(fn.GetTable("TI_BAPICONDHD"), tables.Headers);
                AppendRows(fn.GetTable("TI_BAPICONDIT"), tables.Items);
            }
            if (batch[0].Tables.PhysicalDeletion) fn.SetValue("PI_PHYSICAL_DELETION", "X");
            if (fn.Metadata.TryNameToIndex("IV_DO_COMMIT") >= 0) fn.SetValue("IV_DO_COMMIT", "X");
        }
        catch (Exception ex) when (ex is not RfcCommunicationException)
        {
            // Chưa gửi lô → ghi từng record (lỗi dữ liệu sẽ hiện đúng ở record gây lỗi).
            _logger.LogWarning(ex, "{BapiName}: không chuẩn bị được lô {Count} bảng giá — chuyển sang ghi từng record",
                _options.PriceBapiFunction, batch.Length);
            return BatchOutcome.Rejected;
        }

        ctx.Sent = true;
        try
        {
            fn.InvokeAudited(dest);
        }
        catch (RfcAbapBaseException ex)
        {
            // Lỗi ABAP trong vỏ RFC / BAPI → LUW bị hủy, chưa commit.
            _logger.LogWarning(ex, "{BapiName}: lô {Count} bảng giá lỗi ABAP — chuyển sang ghi từng record", _options.PriceBapiFunction, batch.Length);
            return BatchOutcome.Rejected;
        }
        catch (Exception ex)
        {
            lostReference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            _logger.LogError(ex, "{BapiName}: mất kết nối / lỗi không rõ khi ghi lô {Count} bảng giá (ref {Reference})",
                _options.PriceBapiFunction, batch.Length, lostReference);
            return BatchOutcome.Lost;
        }

        var sapMessages = ReadReturn(fn.GetTable("TO_BAPIRET2"));
        if (fn.GetString("EV_COMMITTED") != "X" || sapMessages.Any(m => m.IsError))
        {
            var first = sapMessages.FirstOrDefault(m => m.IsError);
            _logger.LogInformation("{BapiName}: lô {Count} bảng giá bị từ chối ({Error}) — ghi từng record để tìm record lỗi",
                _options.PriceBapiFunction, batch.Length, first is null ? "không commit" : $"{first.Id}/{first.Number} {first.Message}");
            return BatchOutcome.Rejected;
        }

        foreach (var call in batch.Where(c => c.Knumh is not null)) numbers[call] = call.Knumh;
        foreach (IRfcStructure r in fn.GetTable("TO_BAPIKNUMHS"))
            if (temporary.TryGetValue(r.GetString("COND_NO_OLD").Trim(), out var created))
                numbers[created] = RfcTableReader.NullIfEmpty(r.GetString("COND_NO_NEW"));
        return BatchOutcome.Committed;
    }

    /// <summary>Lô đã commit: đọc lại 1 lần cho cả lô rồi xác nhận từng record.</summary>
    private void ConfirmBatch(RfcDestination dest, BapiRecordCall[] batch, Dictionary<BapiRecordCall, string?> numbers,
        List<SapReturnMessage> messages, Action<bool, string?> done)
    {
        Dictionary<string, RecordLookup>? after = null;
        string? readError = null;
        var toVerify = batch.Where(c => c.Verify is not null && numbers.GetValueOrDefault(c) is not null).ToList();
        if (toVerify.Count > 0)
        {
            try
            {
                after = ReadRecordsByNumbers(dest, toVerify.Select(c => numbers[c]!));
            }
            catch (Exception ex)
            {
                readError = LogReference(ex, toVerify[0]);
            }
        }

        foreach (var call in batch)
        {
            var knumh = numbers.GetValueOrDefault(call);
            if (knumh is null)
            {
                messages.Add(Final("E", PriceErrorCodes.NotApplied, call,
                    "SAP đã commit lô nhưng không trả số condition record cho dòng này — kiểm tra VK13.", null));
                done(false, null);
                continue;
            }
            if (call.Verify is not null)
            {
                if (readError is not null)
                {
                    messages.Add(Final("E", PriceErrorCodes.NotApplied, call,
                        $"SAP đã commit nhưng không đọc lại được để xác nhận — kiểm tra VK13. Mã tham chiếu log: {readError}.", knumh));
                    done(false, knumh);
                    continue;
                }
                if (!call.Verify(after![knumh].Periods))
                {
                    messages.Add(Final("E", PriceErrorCodes.NotApplied, call,
                        "SAP báo thành công nhưng đọc lại chưa thấy trạng thái mới — kiểm tra VK13.", knumh));
                    done(false, knumh);
                    continue;
                }
            }
            messages.Add(Final("S", PriceErrorCodes.Ok, call, call.SuccessText(knumh), knumh));
            done(true, knumh);
        }
    }

    /// <summary>1 record / 1 lần gọi vỏ RFC (lô 1 record, hoặc ghi lại từng record khi lô bị từ chối).</summary>
    private (CallState State, string? Knumh) InvokeSingle(
        RfcDestination dest, CallContext ctx, BapiRecordCall call, List<SapReturnMessage> messages, bool idempotent)
    {
        IRfcFunction fn;
        try
        {
            fn = dest.Repository.CreateFunction(_options.PriceBapiFunction);
            AppendRows(fn.GetTable("TI_BAPICONDCT"), call.Tables.ConditionTables);
            AppendRows(fn.GetTable("TI_BAPICONDHD"), call.Tables.Headers);
            AppendRows(fn.GetTable("TI_BAPICONDIT"), call.Tables.Items);
            if (call.Tables.PhysicalDeletion) fn.SetValue("PI_PHYSICAL_DELETION", "X");
            // Commit trong vỏ RFC là bắt buộc với lời gọi stateless — truyền tường minh, không dựa vào DEFAULT.
            if (fn.Metadata.TryNameToIndex("IV_DO_COMMIT") >= 0) fn.SetValue("IV_DO_COMMIT", "X");
        }
        catch (Exception ex) when (ex is not RfcCommunicationException)
        {
            // Chưa gửi record này sang SAP.
            messages.Add(Final("E", PriceErrorCodes.ValidationError, call,
                $"Không chuẩn bị được dữ liệu gửi SAP — chưa ghi record này. Mã tham chiếu log: {LogReference(ex, call)}.", call.Knumh));
            return (CallState.Failed, null);
        }

        ctx.Sent = true;
        try
        {
            fn.InvokeAudited(dest);
        }
        catch (RfcAbapBaseException ex)
        {
            // Lỗi ABAP trong vỏ RFC / BAPI → LUW bị hủy, chưa commit.
            messages.Add(Final("E", PriceErrorCodes.SapError, call, ex.Message, call.Knumh));
            return (CallState.Failed, null);
        }
        catch (Exception ex)
        {
            // Mất kết nối hoặc lỗi không rõ khi SAP đang xử lý → không biết record đã ghi chưa.
            var reason = ex is RfcCommunicationException ? "Mất kết nối SAP khi đang ghi" : "Lỗi không rõ khi đang ghi";
            messages.Add(Final("E", SapErrorCodes.UnknownOutcome, call,
                $"{reason} — {(idempotent ? "gửi lại an toàn" : "kiểm tra VK13 trước khi gửi lại")}. Mã tham chiếu log: {LogReference(ex, call)}.",
                call.Knumh));
            return (CallState.Lost, null);
        }

        var sapMessages = ReadReturn(fn.GetTable("TO_BAPIRET2")).Select(m => m with { Item = call.Item }).ToList();
        messages.AddRange(sapMessages);
        var committed = fn.GetString("EV_COMMITTED") == "X";
        var firstError = sapMessages.FirstOrDefault(m => m.IsError);
        if (!committed || firstError is not null)
        {
            // Vỏ RFC chặn ở bước kiểm tra trên SAP → mã lỗi RDM ở PARAMETER; lỗi BAPI → PRICE_SAP_ERROR + mã SAP ID/NUMBER.
            var code = firstError is { Id: "ZRDM", Parameter: { Length: > 0 } p } ? p : PriceErrorCodes.SapError;
            messages.Add(Final("E", code, call,
                firstError is null ? "SAP không commit (không có message lỗi)." : $"{firstError.Id}/{firstError.Number}: {firstError.Message}",
                call.Knumh, sapCode: firstError is null ? null : $"{firstError.Id}/{firstError.Number}"));
            return (CallState.Failed, null);
        }

        var knumh = call.Knumh;
        if (knumh is null)
            foreach (IRfcStructure r in fn.GetTable("TO_BAPIKNUMHS"))
                knumh ??= RfcTableReader.NullIfEmpty(r.GetString("COND_NO_NEW"));

        if (call.Verify is not null && knumh is not null)
        {
            bool applied;
            try
            {
                applied = call.Verify(ReadRecordsByNumbers(dest, new[] { knumh })[knumh].Periods);
            }
            catch (Exception ex)
            {
                // Đã commit nhưng không đọc lại được → không khẳng định thành công.
                messages.Add(Final("E", PriceErrorCodes.NotApplied, call,
                    $"SAP đã commit nhưng không đọc lại được để xác nhận — kiểm tra VK13. Mã tham chiếu log: {LogReference(ex, call)}.", knumh));
                return (CallState.Failed, knumh);
            }
            if (!applied)
            {
                messages.Add(Final("E", PriceErrorCodes.NotApplied, call,
                    "SAP báo thành công nhưng đọc lại chưa thấy trạng thái mới — kiểm tra VK13.", knumh));
                return (CallState.Failed, knumh);
            }
        }

        messages.Add(Final("S", PriceErrorCodes.Ok, call, call.SuccessText(knumh), knumh));
        return (CallState.Ok, knumh);
    }


    /// <summary>Ghi exception đầy đủ vào log, trả mã tham chiếu ngắn để đưa vào message (không lộ chi tiết kỹ thuật ra client).</summary>
    private string LogReference(Exception ex, BapiRecordCall call)
    {
        var reference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        _logger.LogError(ex, "{BapiName}: lỗi record {Item} (KNUMH {Knumh}, ref {Reference})",
            _options.PriceBapiFunction, call.Item, call.Knumh, reference);
        return reference;
    }

    private static SapReturnMessage Final(string type, string code, BapiRecordCall call, string text, string? knumh, string? sapCode = null) =>
        new(type, "RDM", code, text, MessageV1: knumh, MessageV2: sapCode, Item: call.Item);

    /// <summary>Vỏ RFC chưa được tạo trên SAP → lỗi rõ ràng thay vì lỗi RFC chung (chưa gửi gì).</summary>
    private SapCallResult? BapiFunctionMissing(RfcDestination dest)
    {
        try
        {
            dest.Repository.GetFunctionMetadata(_options.PriceBapiFunction);
            return null;
        }
        catch (RfcBaseException ex) when (ex is not (RfcCommunicationException or RfcLogonException or RfcResourceException))
        {
            _logger.LogWarning(ex, "{BapiName}: chưa có trên SAP", _options.PriceBapiFunction);
            return SapCallResult.Failed(PriceErrorCodes.FunctionMissing,
                $"Hàm RFC {_options.PriceBapiFunction} (vỏ của BAPI_PRICES_CONDITIONS — BAPI này không remote-enabled) chưa có trên SAP. " +
                "Tạo theo abap/Z_ESD016_PRICES_CONDITIONS.md.");
        }
    }

    private PriceBapiItemValues CurrentValues(RfcDestination dest, PriceRecord r) => new(
        r.Amount ?? 0m, r.Currency ?? "VND", r.PricingUnit ?? 1, r.Unit ?? string.Empty,
        r.CalculationType ?? CalculationType(dest, r.Key.ConditionType), r.ReleaseStatus, r.Deleted);

    /// <summary>(field khóa / đơn vị / tiền tệ) → bảng kiểm tra trên SAP.</summary>
    private static readonly (string Field, string Label, string Table, string Column, string? Where)[] MasterDataChecks =
    {
        ("VKORG", "Sales org", "TVKO", "VKORG", null),
        ("VKORGAU", "Sales org (đơn hàng)", "TVKO", "VKORG", null),
        ("VTWEG", "Distribution channel", "TVTW", "VTWEG", null),
        ("WERKS", "Plant", "T001W", "WERKS", null),
        ("VSBED", "Shipping condition", "TVSB", "VSBED", null),
        ("INCO1", "Incoterms", "TINC", "INCO1", null),
        ("REGIO", "Region", "T005S", "BLAND", "LAND1 = 'VN'"),
        ("MATNR", "Material", "MARA", "MATNR", "LVORM = ' '"),       // chưa đánh dấu xóa (như vỏ RFC v3 / v4)
        ("KUNAG", "Sold-to", "KNA1", "KUNNR", "LOEVM = ' '"),
        ("KUNWE", "Ship-to", "KNA1", "KUNNR", "LOEVM = ' '"),
        ("$UNIT", "Đơn vị", "T006", "MSEHI", null),
        ("$CURR", "Tiền tệ", "TCURC", "WAERS", null)
    };

    /// <summary>
    /// Kiểm tra mã master data tồn tại trên SAP — BAPI_PRICES_CONDITIONS không tự kiểm tra (DRF-930: tạo được bảng giá với đơn vị "ZZZ").
    /// Đọc theo lô (1 lần RFC_READ_TABLE / bảng / 50 giá trị).
    /// </summary>
    private List<SapReturnMessage> ValidateMasterData(
        RfcDestination dest, IEnumerable<(int Index, IReadOnlyDictionary<string, string> Key, string? Unit, string? Currency)> lines)
    {
        var list = lines.ToList();
        string? Value((int Index, IReadOnlyDictionary<string, string> Key, string? Unit, string? Currency) l, string field) => field switch
        {
            "$UNIT" => l.Unit,
            "$CURR" => l.Currency,
            _ => l.Key.TryGetValue(field, out var v) && v.Length > 0 ? v : null
        };

        var messages = new List<SapReturnMessage>();
        foreach (var check in MasterDataChecks)
        {
            var values = list.Select(l => Value(l, check.Field)).Where(v => v is not null).Select(v => v!).Distinct().ToList();
            if (values.Count == 0) continue;

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in values.Chunk(50))
            {
                var where = RfcTableReader.AnyOf(check.Column, chunk).ToList();
                if (check.Where is not null) where.Add("AND " + check.Where);
                foreach (var r in RfcTableReader.Read(dest, check.Table, new[] { check.Column }, where, 1000))
                    found.Add(r[check.Column]);
            }

            foreach (var l in list)
                if (Value(l, check.Field) is { } v && !found.Contains(v))
                    messages.Add(new SapReturnMessage("E", "RDM", PriceErrorCodes.InvalidMasterData,
                        $"{check.Label} '{SapMasterDataClient.ExternalCode(v)}' {(check.Where is { } w && (w.StartsWith("LVORM") || w.StartsWith("LOEVM")) ? "không tồn tại hoặc đã đánh dấu xóa" : "không tồn tại")} trên SAP ({check.Table}).",
                        Field: check.Field.TrimStart('$'), Item: l.Index.ToString()));
        }
        return messages;
    }

    /// <summary>KRECH của loại điều kiện (T685A); DRF-930: cả 4 loại = C (theo số lượng).</summary>
    private string CalculationType(RfcDestination dest, string conditionType) =>
        _calculationTypes.GetOrAdd(conditionType, ct =>
        {
            var row = RfcTableReader.Read(dest, "T685A", new[] { "KSCHL", "KRECH" },
                new[] { RfcTableReader.Condition("KAPPL", PriceConditionRules.Application), RfcTableReader.Condition("KSCHL", ct, and: true) }, 1)
                .FirstOrDefault();
            return row is null || row["KRECH"].Length == 0 ? "C" : row["KRECH"];
        });
}
