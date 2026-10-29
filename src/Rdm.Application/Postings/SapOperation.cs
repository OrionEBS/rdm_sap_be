namespace Rdm.Application.Postings;

/// <summary>Nghiệp vụ ghi sang SAP mà API hỗ trợ.</summary>
public enum SapOperation
{
    /// <summary>Z_EFI011_CREATE_FB01 — chứng từ FI 2 dòng (GL + khách hàng).</summary>
    Fb01,
    /// <summary>Z_EFI009_CREATE_FB75 — Customer credit memo.</summary>
    Fb75,
    /// <summary>BAPI_ACC_DOCUMENT_REV_POST — đảo chứng từ kế toán.</summary>
    AccDocumentReversal,
    /// <summary>Z_ESD014_CREATE_VA01 — Credit memo request (VA01, loại ZVCR).</summary>
    Va01,
    /// <summary>Z_ESD012_CREATE_VBN1 — Condition record free goods (VBN1, loại ZA00).</summary>
    Vbn1,
    /// <summary>Z_ESD013_CREATE_VA41 — Free goods contract (VA41, loại ZVCF).</summary>
    Va41,
    /// <summary>BAPI_MATERIAL_SAVEDATA — đổi tax classification vật tư (MLAN-TAXM1), thay MM17.</summary>
    MaterialTaxClass,
    /// <summary>BAPI_ACC_DOCUMENT_POST — chứng từ FI nhiều dòng (G/L, khách hàng, thuế).</summary>
    AccDocumentPost,
    /// <summary>BAPI_CONTRACT_CREATEFROMDATA — tạo sale contract.</summary>
    ContractCreate,
    /// <summary>BAPI_CUSTOMERCONTRACT_CHANGE — sửa sale contract.</summary>
    ContractChange,
    /// <summary>BAPI_CUSTOMERCONTRACT_CHANGE — duyệt sale contract (gỡ block duyệt).</summary>
    ContractApprove,
    /// <summary>Z_ESD015_PRICE_UPLOAD (VK11) — tạo bảng giá.</summary>
    PriceCreate,
    /// <summary>Z_ESD015_PRICE_UPLOAD (VK12) — sửa bảng giá.</summary>
    PriceChange,
    /// <summary>Z_ESD015_PRICE_UPLOAD (VK12 processing status 02) — duyệt bảng giá.</summary>
    PriceApprove,
    /// <summary>Z_ESD015_PRICE_UPLOAD — tạo bảng giá mới từ bảng giá cũ theo VAT%.</summary>
    PriceCopyWithVat,
    /// <summary>BAPI_PRICES_CONDITIONS (qua Z_ESD016_PRICES_CONDITIONS) — tạo bảng giá (009).</summary>
    PriceBapiCreate,
    /// <summary>BAPI_PRICES_CONDITIONS — sửa bảng giá (004).</summary>
    PriceBapiChange,
    /// <summary>BAPI_PRICES_CONDITIONS — hủy hiệu lực (đánh dấu xóa, 004 + INDIDELETE).</summary>
    PriceBapiInvalidate,
    /// <summary>BAPI_PRICES_CONDITIONS — duyệt (release status A → trống).</summary>
    PriceBapiApprove,
    /// <summary>BAPI_PRICES_CONDITIONS — gỡ duyệt (release status trống → A).</summary>
    PriceBapiUnapprove
}

public static class SapOperationExtensions
{
    public static string FunctionName(this SapOperation op) => op switch
    {
        SapOperation.Fb01 => "Z_EFI011_CREATE_FB01",
        SapOperation.Fb75 => "Z_EFI009_CREATE_FB75",
        SapOperation.AccDocumentReversal => "BAPI_ACC_DOCUMENT_REV_POST",
        SapOperation.Va01 => "Z_ESD014_CREATE_VA01",
        SapOperation.Vbn1 => "Z_ESD012_CREATE_VBN1",
        SapOperation.Va41 => "Z_ESD013_CREATE_VA41",
        SapOperation.MaterialTaxClass => "BAPI_MATERIAL_SAVEDATA",
        SapOperation.AccDocumentPost => "BAPI_ACC_DOCUMENT_POST",
        SapOperation.ContractCreate => "BAPI_CONTRACT_CREATEFROMDATA",
        SapOperation.ContractChange or SapOperation.ContractApprove => "BAPI_CUSTOMERCONTRACT_CHANGE",
        SapOperation.PriceCreate or SapOperation.PriceChange or SapOperation.PriceApprove or SapOperation.PriceCopyWithVat
            => "Z_ESD015_PRICE_UPLOAD",
        SapOperation.PriceBapiCreate or SapOperation.PriceBapiChange or SapOperation.PriceBapiInvalidate
            or SapOperation.PriceBapiApprove or SapOperation.PriceBapiUnapprove => "Z_ESD016_PRICES_CONDITIONS",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
    };
}

/// <summary>Trạng thái 1 lần ghi sang SAP.</summary>
public enum PostingStatus
{
    /// <summary>Đang gọi SAP.</summary>
    Pending,
    /// <summary>SAP đã tạo chứng từ.</summary>
    Succeeded,
    /// <summary>SAP chắc chắn KHÔNG tạo chứng từ (lỗi nghiệp vụ / không kết nối được) — được phép gửi lại.</summary>
    Failed,
    /// <summary>Không xác định được SAP đã tạo chứng từ hay chưa (mất kết nối giữa chừng...) — phải kiểm tra SAP trước khi gửi lại.</summary>
    Unknown
}

/// <summary>Mã lỗi trả về cho client khi Status = Failed / Unknown.</summary>
public static class SapErrorCodes
{
    /// <summary>SAP trả message E/A trong RETURN hoặc ABAP raise lỗi.</summary>
    public const string Business = "SAP_BUSINESS";
    /// <summary>Không kết nối được SAP trước khi gửi lệnh — chưa có gì được ghi.</summary>
    public const string Unavailable = "SAP_UNAVAILABLE";
    /// <summary>Đăng nhập SAP thất bại (user/pass/khóa user).</summary>
    public const string Logon = "SAP_LOGON";
    /// <summary>Dữ liệu không chuyển được sang kiểu field SAP.</summary>
    public const string InvalidData = "SAP_INVALID_DATA";
    /// <summary>Không xác định kết quả — xem <see cref="PostingStatus.Unknown"/>.</summary>
    public const string UnknownOutcome = "SAP_UNKNOWN_OUTCOME";
    /// <summary>Lỗi không lường trước phía API.</summary>
    public const string Internal = "INTERNAL";
}
