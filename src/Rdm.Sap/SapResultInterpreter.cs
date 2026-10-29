using Rdm.Application.Postings;

namespace Rdm.Sap;

/// <summary>
/// Diễn giải BAPIRET2 → kết quả. Thuần logic, không phụ thuộc NCo (unit-test được).
///
/// Hành vi phía SAP (đọc từ source LZIFI_FG_RDMF01 / LZISD_FG_RDMF02 trên DEV):
///   - FB01/FB75: BAPI_ACC_DOCUMENT_POST rồi tự COMMIT; RETURN thành công = S RW 605
///     "Document posted successfully: BKPFF &lt;BELNR+BUKRS+GJAHR&gt; &lt;LOGSYS&gt;" (V1 = OBJ_TYPE, V2 = OBJ_KEY, V3 = OBJ_SYS).
///   - VA01 / VA41: SD_SALESDOCUMENT_CREATE / BAPI_CONTRACT_CREATEFROMDATA rồi tự COMMIT;
///     thành công = S V1 311 "&lt;loại&gt; &lt;VBELN&gt; has been saved" (V2 = VBELN).
///   - VBN1: batch input, xem <see cref="InterpretBatchInput"/>.
/// </summary>
public static class SapResultInterpreter
{
    public static SapDocumentRef? ExtractDocument(IReadOnlyList<SapReturnMessage> messages)
    {
        var rw = messages.FirstOrDefault(m => m.Type == "S" && m.Id == "RW" && m.Number == "605"
                                              && !string.IsNullOrWhiteSpace(m.MessageV2));
        if (rw is not null)
            return FromObjectKey(rw.MessageV1, rw.MessageV2!, rw.MessageV3);

        var sd = messages.FirstOrDefault(m => m.Type == "S" && m.Id == "V1" && m.Number == "311"
                                              && !string.IsNullOrWhiteSpace(m.MessageV2));
        if (sd is not null)
            return new SapDocumentRef(sd.MessageV2!.Trim());

        return null;
    }

    /// <summary>
    /// OBJ_KEY của chứng từ FI (AWTYP BKPF/BKPFF) = BELNR(10) + BUKRS(4) + GJAHR(4).
    /// </summary>
    public static SapDocumentRef FromObjectKey(string? objectType, string objectKey, string? objectSystem)
    {
        var type = string.IsNullOrWhiteSpace(objectType) ? null : objectType.Trim();
        var sys = string.IsNullOrWhiteSpace(objectSystem) ? null : objectSystem.Trim();
        var key = objectKey.TrimEnd();

        if (type is "BKPFF" or "BKPF" && key.Length == 18)
            return new SapDocumentRef(key[..10], key[10..14].Trim(), key[14..18], type, key, sys);

        return new SapDocumentRef(key.Length > 10 ? key[..10] : key, null, null, type, key, sys);
    }

    /// <summary>
    /// Diễn giải kết quả của các hàm Z tự COMMIT bên trong (FB01/FB75/VA01).
    /// Có số chứng từ = SAP đã ghi, kể cả khi kèm message lỗi phụ.
    /// </summary>
    public static SapCallResult InterpretSelfCommitting(IReadOnlyList<SapReturnMessage> messages)
    {
        var document = ExtractDocument(messages);
        if (document is not null)
            return SapCallResult.Success(document, messages);

        var errors = messages.Where(m => m.IsError).ToList();
        if (errors.Count > 0)
            return SapCallResult.Failed(SapErrorCodes.Business, string.Join("; ", errors.Select(e => e.Message)), messages);

        return SapCallResult.Unknown(
            "SAP không trả lỗi nhưng cũng không trả số chứng từ — kiểm tra trên SAP trước khi gửi lại.", messages);
    }

    public sealed record MaterialSaveOutcome(bool Ok, bool Changed, SapReturnMessage? KeyMessage);

    /// <summary>
    /// BAPI_MATERIAL_SAVEDATA: RETURN luôn S MM/356 → quyết định theo RETURNMESSAGES:
    /// E/A bất kỳ = lỗi; S M3/801 = đã đổi; S M3/810 = không đổi.
    /// </summary>
    public static MaterialSaveOutcome InterpretMaterialSave(SapReturnMessage? ret, IReadOnlyList<SapReturnMessage> details)
    {
        var error = details.FirstOrDefault(m => m.IsError) ?? (ret is { IsError: true } ? ret : null);
        if (error is not null) return new MaterialSaveOutcome(false, false, error);

        var changed = details.FirstOrDefault(m => m.Id == "M3" && m.Number == "801");
        if (changed is not null) return new MaterialSaveOutcome(true, true, changed);

        var noChange = details.FirstOrDefault(m => m.Id == "M3" && m.Number == "810");
        if (noChange is not null) return new MaterialSaveOutcome(true, false, noChange);

        return ret is { Type: "S" }
            ? new MaterialSaveOutcome(true, true, ret)
            : new MaterialSaveOutcome(false, false, ret);
    }

    /// <summary>
    /// Z_ESD012_CREATE_VBN1 (CALL TRANSACTION 'VBN1' ... UPDATE 'S'): thành công = S VK 382
    /// "Condition records for Free goods saved" (không có số chứng từ). Không có 382 → hàm Z đổi mọi message thành E.
    /// Message đã được hàm Z format sẵn vào MSGV1.
    /// </summary>
    public static SapCallResult InterpretBatchInput(IReadOnlyList<SapReturnMessage> messages)
    {
        if (messages.Any(m => m.Type == "S" && m.Id == "VK" && m.Number == "382"))
            return SapCallResult.Success(new SapDocumentRef(null, ObjectType: "VBN1"), messages);

        if (messages.Count > 0)
            return SapCallResult.Failed(SapErrorCodes.Business,
                string.Join("; ", messages.Select(m => m.Message).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct()), messages);

        return SapCallResult.Unknown(
            "VBN1 không trả message nào — kiểm tra condition record trên SAP trước khi gửi lại.", messages);
    }
}
