using Rdm.Application.Postings;
using Rdm.Application.Requests;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Đổi tax classification 1 (MLAN-TAXM1) qua BAPI_MATERIAL_SAVEDATA — thay MM17.
///
/// Hành vi BAPI kiểm chứng trên DRF-930:
///   - RETURN luôn là S MM/356 "created or extended" kể cả khi không đổi gì → phải đọc RETURNMESSAGES:
///     S M3/801 "Material … changed", S M3/810 "No changes made", E M3/014 "Tax category … does not define classification …".
///   - Mã vật tư không tồn tại → BAPI tìm cách TẠO vật tư (E M3/098 "Enter a material type") → kiểm tra MARA trước.
///   - Không cần bật view nào trong HEADDATA để đổi MLAN.
/// "Đặt TAXM1 = X" là idempotent → lỗi / mất kết nối giữa chừng trả Failed (gửi lại an toàn), không trả Unknown.
/// </summary>
public sealed partial class SapClient
{
    private const string MaterialSaveFunction = "BAPI_MATERIAL_SAVEDATA";
    private const int MaterialChunkSize = 100;

    public Task<SapCallResult> UpdateMaterialTaxClassAsync(MaterialTaxClassRequest request, CancellationToken ct) =>
        RunAsync(MaterialSaveFunction, stateful: true, (dest, ctx) =>
        {
            var country = SapRequestMapper.Code(request.Country) ?? "VN";
            var category = SapRequestMapper.Code(request.TaxCategory) ?? ReadFirstTaxCategory(dest, country);
            if (category is null)
                return SapCallResult.Failed(SapErrorCodes.Business, $"Quốc gia {country} không có loại thuế nào trong TSTL.");

            var items = request.Items.Select(i => new TaxItem(
                    i.Material.Trim(),
                    InternalMaterialNumber(i.Material),
                    SapRequestMapper.Code(i.TaxClass) ?? SapRequestMapper.Code(request.TaxClass)!))
                .ToList();

            // Đọc trước (chưa ghi gì): vật tư tồn tại + TAXM1 hiện tại.
            var matnrs = items.Select(i => i.Matnr).Distinct().ToList();
            var existing = ReadExistingMaterials(dest, matnrs);
            var current = ReadCurrentTaxClass(dest, matnrs, country);

            var messages = new List<SapReturnMessage>();
            int changed = 0, unchanged = 0, failed = 0;
            var connectionLost = false;

            foreach (var item in items)
            {
                current.TryGetValue(item.Matnr, out var old);
                if (connectionLost)
                {
                    messages.Add(ItemMessage("E", "NOT_PROCESSED", item, old,
                        "Chưa xử lý do mất kết nối SAP — gửi lại request (an toàn, thao tác idempotent)."));
                    failed++;
                    continue;
                }
                if (!existing.Contains(item.Matnr))
                {
                    messages.Add(ItemMessage("E", "NOT_FOUND", item, old, $"Vật tư {item.Input} không tồn tại trên SAP."));
                    failed++;
                    continue;
                }
                if (old == item.TaxClass)
                {
                    messages.Add(ItemMessage("S", "UNCHANGED", item, old, $"TAXM1 đã là {old} — không gọi BAPI."));
                    unchanged++;
                    continue;
                }

                ctx.Sent = true;
                try
                {
                    var fn = dest.Repository.CreateFunction(MaterialSaveFunction);
                    fn.GetStructure("HEADDATA").SetValue("MATERIAL", item.Matnr);
                    var tax = fn.GetTable("TAXCLASSIFICATIONS");
                    tax.Append();
                    tax.SetValue("DEPCOUNTRY", country);
                    tax.SetValue("TAX_TYPE_1", category);
                    tax.SetValue("TAXCLASS_1", item.TaxClass);
                    fn.InvokeAudited(dest);

                    var outcome = SapResultInterpreter.InterpretMaterialSave(
                        ReadReturnRow(fn.GetStructure("RETURN")), ReadReturn(fn.GetTable("RETURNMESSAGES")));
                    var key = outcome.KeyMessage;
                    var sapRef = key is null ? string.Empty : $" ({key.Id}/{key.Number})";

                    if (!outcome.Ok)
                    {
                        Rollback(dest);
                        messages.Add(ItemMessage("E", "FAILED", item, old, (key?.Message ?? "BAPI báo lỗi.") + sapRef));
                        failed++;
                        continue;
                    }

                    var commit = dest.Repository.CreateFunction("BAPI_TRANSACTION_COMMIT");
                    commit.SetValue("WAIT", "X");
                    commit.InvokeAudited(dest);
                    var commitError = ReadReturn(commit.GetStructure("RETURN")).FirstOrDefault(m => m.IsError);
                    if (commitError is not null)
                    {
                        messages.Add(ItemMessage("E", "FAILED", item, old, "BAPI_TRANSACTION_COMMIT: " + commitError.Message));
                        failed++;
                        continue;
                    }

                    if (outcome.Changed)
                    {
                        messages.Add(ItemMessage("S", "CHANGED", item, old, $"TAXM1 {old ?? "(trống)"} → {item.TaxClass}: {key?.Message}{sapRef}"));
                        changed++;
                    }
                    else
                    {
                        messages.Add(ItemMessage("S", "UNCHANGED", item, old, (key?.Message ?? "No changes made") + sapRef));
                        unchanged++;
                    }
                }
                catch (RfcCommunicationException ex)
                {
                    connectionLost = true;
                    messages.Add(ItemMessage("E", "FAILED", item, old,
                        $"Mất kết nối SAP: {ex.Message} — kiểm tra lại hoặc gửi lại request (an toàn, thao tác idempotent)."));
                    failed++;
                }
                catch (RfcBaseException ex)
                {
                    Rollback(dest);
                    messages.Add(ItemMessage("E", "FAILED", item, old, ex.Message));
                    failed++;
                }
            }

            var summary = $"{items.Count} vật tư: {changed} đã đổi, {unchanged} không đổi, {failed} lỗi.";
            var document = new SapDocumentRef(null, ObjectType: "MLAN");
            return failed == 0
                ? SapCallResult.Success(document, messages)
                : SapCallResult.Failed(
                    connectionLost ? SapErrorCodes.Unavailable : SapErrorCodes.Business, summary, messages);
        }, ct);

    private sealed record TaxItem(string Input, string Matnr, string TaxClass);

    private static SapReturnMessage ItemMessage(string type, string number, TaxItem item, string? oldValue, string text) =>
        new(type, "RDM", number, text, MessageV1: oldValue, MessageV2: item.TaxClass,
            Item: SapMasterDataClient.ExternalCode(item.Matnr));

    /// <summary>MATNR toàn số → 18 ký tự có 0 đầu (BAPI không tự chạy conversion exit).</summary>
    internal static string InternalMaterialNumber(string material)
    {
        var m = material.Trim().ToUpperInvariant();
        return m.Length < 18 && m.All(char.IsDigit) ? m.PadLeft(18, '0') : m;
    }

    private static string? ReadFirstTaxCategory(RfcDestination dest, string country)
    {
        var rows = RfcTableReader.Read(dest, "TSTL", new[] { "TALND", "LFDNR", "TATYP" },
            new[] { RfcTableReader.Condition("TALND", country) }, 100);
        return rows.OrderBy(r => r["LFDNR"], StringComparer.Ordinal).Select(r => r["TATYP"]).FirstOrDefault(t => t.Length > 0);
    }

    private static HashSet<string> ReadExistingMaterials(RfcDestination dest, IReadOnlyList<string> matnrs)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in matnrs.Chunk(MaterialChunkSize))
            foreach (var r in RfcTableReader.Read(dest, "MARA", new[] { "MATNR" }, RfcTableReader.AnyOf("MATNR", chunk), 1000))
                result.Add(r["MATNR"]);
        return result;
    }

    private static Dictionary<string, string> ReadCurrentTaxClass(RfcDestination dest, IReadOnlyList<string> matnrs, string country)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in matnrs.Chunk(MaterialChunkSize))
        {
            var where = RfcTableReader.AnyOf("MATNR", chunk).Append(RfcTableReader.Condition("ALAND", country, and: true)).ToList();
            foreach (var r in RfcTableReader.Read(dest, "MLAN", new[] { "MATNR", "TAXM1" }, where, 1000))
                result[r["MATNR"]] = r["TAXM1"];
        }
        return result;
    }
}
