namespace Rdm.Application.MasterData;

/// <summary>Các nhóm master data SD đồng bộ từ SAP về DB local (schema md).</summary>
public enum MasterDataObject
{
    /// <summary>KNA1 + KNVP (PARVW = AG): khách hàng là sold-to ở ít nhất 1 sales area, chưa bị xóa.</summary>
    SoldTo,
    /// <summary>KNA1 của các khách là partner WE của sold-to: danh mục ship-to (1 dòng / ship-to).</summary>
    ShipTo,
    /// <summary>KNVP (PARVW = WE): quan hệ sold-to ↔ ship-to theo sales area.</summary>
    SoldToShipTo,
    /// <summary>T171 / T171T.</summary>
    SalesDistrict,
    /// <summary>TVBUR / TVKBT + TVKBZ (gán sales office ↔ sales area).</summary>
    SalesOffice,
    /// <summary>TVSB / TVSBT.</summary>
    ShippingCondition,
    /// <summary>TVKO / TVKOT.</summary>
    SalesOrg,
    /// <summary>T001W — chỉ quốc gia trong Sap:PlantCountries (mặc định VN).</summary>
    Plant,
    /// <summary>T042Z.</summary>
    PaymentMethod,
    /// <summary>T173 / T173T — shipping type (10T Truck, 20T Truck...).</summary>
    VehicleType,
    /// <summary>TVV3 / TVV3T.</summary>
    CustomerGroup3,
    /// <summary>MARA (chỉ loại vật tư trong Sap:MaterialTypes, mặc định ZFNP) + MAKT (tên EN / VI) + T006A (đơn vị tính).</summary>
    Material,
    /// <summary>T023 / T023T.</summary>
    MaterialGroup,
    /// <summary>T134 / T134T.</summary>
    MaterialType,
    /// <summary>TSPA / TSPAT.</summary>
    Division,
    /// <summary>BAPI_GL_ACC_GETLIST — chỉ company code thuộc COA Sap:ChartOfAccounts (1000) và CO area Sap:ControllingArea (G100).</summary>
    GlAccount,
    /// <summary>BAPI_COSTCENTER_GETLIST1 — chỉ CO area Sap:ControllingArea (G100).</summary>
    CostCenter
}

/// <summary>Mọi bảng master data có thời điểm đồng bộ.</summary>
public interface IMasterDataRow
{
    DateTime SyncedAtUtc { get; set; }
}

public sealed class SoldToParty : IMasterDataRow
{
    /// <summary>KUNNR.</summary>
    public string CustomerNumber { get; set; } = string.Empty;
    /// <summary>Tên đầy đủ = NAME2 + NAME3 + NAME4 (quy ước trên SAP: NAME1 là tên ngắn); trống thì lấy NAME1.</summary>
    public string CustomerName { get; set; } = string.Empty;
    public string? Name1 { get; set; }
    public string? Name2 { get; set; }
    public string? Name3 { get; set; }
    public string? Name4 { get; set; }
    /// <summary>SORTL — Search term.</summary>
    public string? SearchTerm { get; set; }
    /// <summary>Mã số thuế = STCEG, trống thì STCD1.</summary>
    public string? TaxNumber { get; set; }
    /// <summary>STCD1.</summary>
    public string? TaxNumber1 { get; set; }
    /// <summary>STCEG — VAT registration number.</summary>
    public string? VatRegistrationNumber { get; set; }
    /// <summary>KTOKD — Account group (loại khách hàng), vd. Z101.</summary>
    public string CustomerType { get; set; } = string.Empty;
    /// <summary>T077X-TXT30, vd. Sold-to Party.</summary>
    public string? CustomerTypeName { get; set; }
    /// <summary>ERDAT.</summary>
    public DateOnly? CreatedOn { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

/// <summary>Danh mục ship-to (1 dòng / ship-to) — các khách là partner WE của ít nhất 1 sold-to.</summary>
public sealed class ShipToParty : IMasterDataRow
{
    /// <summary>KUNNR.</summary>
    public string CustomerNumber { get; set; } = string.Empty;
    /// <summary>Tên đầy đủ = NAME2 + NAME3 + NAME4; trống thì NAME1.</summary>
    public string CustomerName { get; set; } = string.Empty;
    public string? Name1 { get; set; }
    public string? Name2 { get; set; }
    public string? Name3 { get; set; }
    public string? Name4 { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public string? Phone { get; set; }
    public string? SearchTerm { get; set; }
    /// <summary>Mã số thuế = STCEG, trống thì STCD1.</summary>
    public string? TaxNumber { get; set; }
    /// <summary>KTOKD — Account group.</summary>
    public string CustomerType { get; set; } = string.Empty;
    public string? CustomerTypeName { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

/// <summary>Quan hệ sold-to ↔ ship-to theo sales area (KNVP, PARVW = WE).</summary>
public sealed class SoldToShipTo : IMasterDataRow
{
    /// <summary>KNVP-KUNNR.</summary>
    public string SoldTo { get; set; } = string.Empty;
    /// <summary>VKORG.</summary>
    public string SalesOrg { get; set; } = string.Empty;
    /// <summary>VTWEG.</summary>
    public string DistributionChannel { get; set; } = string.Empty;
    /// <summary>SPART.</summary>
    public string Division { get; set; } = string.Empty;
    /// <summary>PARZA — Partner counter.</summary>
    public int PartnerCounter { get; set; }
    /// <summary>KUNN2 — Ship-to.</summary>
    public string ShipTo { get; set; } = string.Empty;
    /// <summary>DEFPA — Default partner.</summary>
    public bool IsDefault { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

/// <summary>Bảng mã có text EN / VI.</summary>
public abstract class CodeTextRow : IMasterDataRow
{
    public string Code { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public string? NameVi { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class SalesDistrict : CodeTextRow;
public sealed class ShippingCondition : CodeTextRow;
public sealed class CustomerGroup3 : CodeTextRow;

public sealed class SalesOffice : CodeTextRow
{
    /// <summary>Toàn bộ field TVBUR (JSON).</summary>
    public string? SapData { get; set; }
}

/// <summary>TVKBZ — sales office thuộc sales area nào.</summary>
public sealed class SalesOfficeAssignment : IMasterDataRow
{
    public string SalesOrg { get; set; } = string.Empty;
    public string DistributionChannel { get; set; } = string.Empty;
    public string Division { get; set; } = string.Empty;
    public string SalesOffice { get; set; } = string.Empty;
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class SalesOrganization : CodeTextRow
{
    /// <summary>WAERS.</summary>
    public string? Currency { get; set; }
    /// <summary>BUKRS.</summary>
    public string? CompanyCode { get; set; }
    /// <summary>Toàn bộ field TVKO (JSON).</summary>
    public string? SapData { get; set; }
}

public sealed class Plant : IMasterDataRow
{
    /// <summary>WERKS.</summary>
    public string Code { get; set; } = string.Empty;
    public string? Name1 { get; set; }
    public string? Name2 { get; set; }
    public string? SalesOrg { get; set; }
    public string? DistributionChannel { get; set; }
    public string? Division { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    /// <summary>Toàn bộ field T001W (JSON).</summary>
    public string? SapData { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class PaymentMethod : IMasterDataRow
{
    /// <summary>LAND1.</summary>
    public string Country { get; set; } = string.Empty;
    /// <summary>ZLSCH.</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>TEXT1.</summary>
    public string? Description { get; set; }
    /// <summary>Toàn bộ field T042Z (JSON).</summary>
    public string? SapData { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class VehicleType : CodeTextRow
{
    /// <summary>Toàn bộ field T173 (JSON).</summary>
    public string? SapData { get; set; }
}

/// <summary>Vật tư (MARA + MAKT).</summary>
public sealed class Material : IMasterDataRow
{
    /// <summary>MATNR dạng lưu trong SAP (có 0 đầu), vd. 000000000000100016.</summary>
    public string MaterialNumber { get; set; } = string.Empty;
    /// <summary>Mã hiển thị (bỏ 0 đầu nếu toàn số), vd. 100016.</summary>
    public string MaterialCode { get; set; } = string.Empty;
    /// <summary>MAKT-MAKTX tiếng Anh.</summary>
    public string? NameEn { get; set; }
    /// <summary>MAKT-MAKTX tiếng Việt.</summary>
    public string? NameVi { get; set; }
    /// <summary>MEINS — đơn vị tính cơ sở (mã nội bộ SAP, dùng khi gọi BAPI), vd. ST, TO.</summary>
    public string BaseUnit { get; set; } = string.Empty;
    /// <summary>T006A-MSEH3 — mã đơn vị hiển thị, vd. PC, MT.</summary>
    public string? BaseUnitExternal { get; set; }
    /// <summary>T006A-MSEHL tiếng Anh.</summary>
    public string? BaseUnitNameEn { get; set; }
    /// <summary>T006A-MSEHL tiếng Việt.</summary>
    public string? BaseUnitNameVi { get; set; }
    /// <summary>MTART.</summary>
    public string? MaterialType { get; set; }
    /// <summary>MATKL.</summary>
    public string? MaterialGroup { get; set; }
    /// <summary>SPART.</summary>
    public string? Division { get; set; }
    /// <summary>LVORM — đánh dấu xóa ở cấp client.</summary>
    public bool IsDeleted { get; set; }
    /// <summary>ERSDA.</summary>
    public DateOnly? CreatedOn { get; set; }
    /// <summary>LAEDA.</summary>
    public DateOnly? ChangedOn { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class MaterialGroup : CodeTextRow
{
    /// <summary>T023T-WGBEZ60 tiếng Anh.</summary>
    public string? LongNameEn { get; set; }
    /// <summary>T023T-WGBEZ60 tiếng Việt.</summary>
    public string? LongNameVi { get; set; }
    /// <summary>Toàn bộ field T023 (JSON).</summary>
    public string? SapData { get; set; }
}

public sealed class MaterialType : CodeTextRow
{
    /// <summary>Toàn bộ field T134 (JSON).</summary>
    public string? SapData { get; set; }
}

public sealed class Division : CodeTextRow;

/// <summary>Tài khoản GL theo company code (BAPI_GL_ACC_GETLIST).</summary>
public sealed class GlAccount : IMasterDataRow
{
    /// <summary>COMP_CODE.</summary>
    public string CompanyCode { get; set; } = string.Empty;
    /// <summary>GL_ACCOUNT dạng lưu trong SAP (có 0 đầu), vd. 0011111000.</summary>
    public string AccountNumber { get; set; } = string.Empty;
    /// <summary>Mã hiển thị (bỏ 0 đầu nếu toàn số), vd. 11111000.</summary>
    public string AccountCode { get; set; } = string.Empty;
    /// <summary>KTOPL — Chart of accounts của company code.</summary>
    public string ChartOfAccounts { get; set; } = string.Empty;
    /// <summary>SHORT_TEXT.</summary>
    public string? ShortText { get; set; }
    /// <summary>LONG_TEXT.</summary>
    public string? LongText { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

/// <summary>Cost center (BAPI_COSTCENTER_GETLIST1).</summary>
public sealed class CostCenter : IMasterDataRow
{
    /// <summary>CO_AREA.</summary>
    public string ControllingArea { get; set; } = string.Empty;
    /// <summary>COSTCENTER.</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>NAME.</summary>
    public string? Name { get; set; }
    /// <summary>DESCRIPT.</summary>
    public string? Description { get; set; }
    /// <summary>ACT_STATE — trạng thái kích hoạt (rỗng = active).</summary>
    public string? ActivationStatus { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

public sealed class MasterDataSyncRun
{
    public long Id { get; set; }
    public MasterDataObject Object { get; set; }
    /// <summary>Api / Schedule.</summary>
    public string Trigger { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; }
    public int RowCount { get; set; }
    public string? Error { get; set; }
}

public sealed record CustomerPartners(
    IReadOnlyList<SoldToParty> SoldTo, IReadOnlyList<ShipToParty> ShipTo, IReadOnlyList<SoldToShipTo> Relations);

public sealed record SalesOfficeData(IReadOnlyList<SalesOffice> Offices, IReadOnlyList<SalesOfficeAssignment> Assignments);
