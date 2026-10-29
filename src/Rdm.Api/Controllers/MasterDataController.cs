using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rdm.Application.Abstractions;
using Rdm.Application.MasterData;
using Rdm.Application.Postings;

namespace Rdm.Api.Controllers;

/// <summary>
/// Master data SD — mỗi bảng 1 cặp API:
///   POST /api/master-data/{bảng}/sync : đọc từ SAP (RFC_READ_TABLE) và thay toàn bộ bảng trong DB local.
///   GET  /api/master-data/{bảng}      : đọc từ DB local.
/// </summary>
[ApiController]
[Route("api/master-data")]
[Produces("application/json")]
public sealed class MasterDataController : ControllerBase
{
    private readonly IMasterDataSyncService _sync;
    private readonly IMasterDataRepository _repo;

    public MasterDataController(IMasterDataSyncService sync, IMasterDataRepository repo)
    {
        _sync = sync;
        _repo = repo;
    }

    // ------------------------------------------------------------ Sold-to (KNA1 + KNVP AG)

    /// <summary>Đồng bộ Sold-to từ SAP (KNA1 + KNVP partner AG, chưa đánh dấu xóa).</summary>
    [HttpPost("sold-to/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncSoldTo(CancellationToken ct) => SyncOne(MasterDataObject.SoldTo, ct);

    /// <summary>Sold-to trong DB local. search: tìm theo mã KH / tên / NAME1 / mã số thuế.</summary>
    [HttpGet("sold-to")]
    public async Task<PagedResult<SoldToParty>> GetSoldTo(
        [FromQuery] string? search, [FromQuery] string? customerType, [FromQuery] int page = 1, [FromQuery] int pageSize = 100,
        CancellationToken ct = default)
    {
        var q = _repo.Query<SoldToParty>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.CustomerNumber.Contains(s) || x.CustomerName.Contains(s) || x.Name1!.Contains(s)
                             || x.SearchTerm!.Contains(s) || x.TaxNumber!.Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(customerType)) q = q.Where(x => x.CustomerType == customerType.Trim().ToUpper());
        return await Page(q.OrderBy(x => x.CustomerNumber), page, pageSize, ct);
    }

    /// <summary>1 Sold-to trong DB local (mã KH toàn số tự thêm 0 đầu).</summary>
    [HttpGet("sold-to/{customer}")]
    [ProducesResponseType<SoldToParty>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SoldToParty>> GetSoldToByNumber(string customer, CancellationToken ct)
    {
        var key = PadCustomer(customer);
        return await _repo.Query<SoldToParty>().FirstOrDefaultAsync(x => x.CustomerNumber == key, ct) is { } s ? s : NotFound();
    }

    // ------------------------------------------------------------ Ship-to (danh mục)

    /// <summary>Đồng bộ danh mục Ship-to từ SAP (KNA1 của các khách là partner WE của sold-to).</summary>
    [HttpPost("ship-to/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncShipTo(CancellationToken ct) => SyncOne(MasterDataObject.ShipTo, ct);

    /// <summary>Danh mục Ship-to trong DB local. search: tìm theo mã / tên / NAME1 / mã số thuế.</summary>
    [HttpGet("ship-to")]
    public async Task<PagedResult<ShipToParty>> GetShipTo(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var q = _repo.Query<ShipToParty>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.CustomerNumber.Contains(s) || x.CustomerName.Contains(s) || x.Name1!.Contains(s)
                             || x.SearchTerm!.Contains(s) || x.TaxNumber!.Contains(s));
        }
        return await Page(q.OrderBy(x => x.CustomerNumber), page, pageSize, ct);
    }

    /// <summary>1 Ship-to trong DB local.</summary>
    [HttpGet("ship-to/{customer}")]
    [ProducesResponseType<ShipToParty>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShipToParty>> GetShipToByNumber(string customer, CancellationToken ct)
    {
        var key = PadCustomer(customer);
        return await _repo.Query<ShipToParty>().FirstOrDefaultAsync(x => x.CustomerNumber == key, ct) is { } s ? s : NotFound();
    }

    // ------------------------------------------------------------ Quan hệ Sold-to ↔ Ship-to (KNVP WE)

    /// <summary>Đồng bộ quan hệ Sold-to ↔ Ship-to từ SAP (KNVP, partner function WE).</summary>
    [HttpPost("sold-to-ship-to/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncSoldToShipTo(CancellationToken ct) => SyncOne(MasterDataObject.SoldToShipTo, ct);

    /// <summary>Quan hệ Sold-to ↔ Ship-to trong DB local, lọc theo sold-to / ship-to / sales area.</summary>
    [HttpGet("sold-to-ship-to")]
    public async Task<PagedResult<SoldToShipTo>> GetSoldToShipTo(
        [FromQuery] string? soldTo, [FromQuery] string? shipTo, [FromQuery] string? salesOrg,
        [FromQuery] string? distributionChannel, [FromQuery] string? division,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var q = _repo.Query<SoldToShipTo>();
        if (!string.IsNullOrWhiteSpace(soldTo)) { var k = PadCustomer(soldTo); q = q.Where(x => x.SoldTo == k); }
        if (!string.IsNullOrWhiteSpace(shipTo)) { var k = PadCustomer(shipTo); q = q.Where(x => x.ShipTo == k); }
        if (!string.IsNullOrWhiteSpace(salesOrg)) q = q.Where(x => x.SalesOrg == salesOrg.Trim().ToUpper());
        if (!string.IsNullOrWhiteSpace(distributionChannel)) q = q.Where(x => x.DistributionChannel == distributionChannel.Trim());
        if (!string.IsNullOrWhiteSpace(division)) q = q.Where(x => x.Division == division.Trim());
        return await Page(q.OrderBy(x => x.SoldTo).ThenBy(x => x.SalesOrg).ThenBy(x => x.DistributionChannel)
            .ThenBy(x => x.Division).ThenBy(x => x.PartnerCounter), page, pageSize, ct);
    }

    // ------------------------------------------------------------ Sales District (T171)

    /// <summary>Đồng bộ Sales District từ SAP (T171 / T171T).</summary>
    [HttpPost("sales-districts/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncSalesDistricts(CancellationToken ct) => SyncOne(MasterDataObject.SalesDistrict, ct);

    /// <summary>Sales District trong DB local.</summary>
    [HttpGet("sales-districts")]
    public Task<List<SalesDistrict>> GetSalesDistricts(CancellationToken ct) =>
        _repo.Query<SalesDistrict>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Sales Office (TVBUR + TVKBZ)

    /// <summary>Đồng bộ Sales Office từ SAP (TVBUR / TVKBT + gán sales area TVKBZ).</summary>
    [HttpPost("sales-offices/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncSalesOffices(CancellationToken ct) => SyncOne(MasterDataObject.SalesOffice, ct);

    /// <summary>Sales Office trong DB local; truyền sales area để chỉ lấy office được gán (TVKBZ).</summary>
    [HttpGet("sales-offices")]
    public Task<List<SalesOffice>> GetSalesOffices(
        [FromQuery] string? salesOrg, [FromQuery] string? distributionChannel, [FromQuery] string? division, CancellationToken ct)
    {
        var q = _repo.Query<SalesOffice>();
        if (!string.IsNullOrWhiteSpace(salesOrg) || !string.IsNullOrWhiteSpace(distributionChannel) || !string.IsNullOrWhiteSpace(division))
        {
            var a = _repo.Query<SalesOfficeAssignment>();
            if (!string.IsNullOrWhiteSpace(salesOrg)) a = a.Where(x => x.SalesOrg == salesOrg.Trim().ToUpper());
            if (!string.IsNullOrWhiteSpace(distributionChannel)) a = a.Where(x => x.DistributionChannel == distributionChannel.Trim());
            if (!string.IsNullOrWhiteSpace(division)) a = a.Where(x => x.Division == division.Trim());
            q = q.Where(o => a.Any(x => x.SalesOffice == o.Code));
        }
        return q.OrderBy(x => x.Code).ToListAsync(ct);
    }

    /// <summary>Gán Sales Office ↔ sales area (TVKBZ) trong DB local.</summary>
    [HttpGet("sales-office-assignments")]
    public Task<List<SalesOfficeAssignment>> GetSalesOfficeAssignments([FromQuery] string? salesOrg, CancellationToken ct)
    {
        var q = _repo.Query<SalesOfficeAssignment>();
        if (!string.IsNullOrWhiteSpace(salesOrg)) q = q.Where(x => x.SalesOrg == salesOrg.Trim().ToUpper());
        return q.OrderBy(x => x.SalesOrg).ThenBy(x => x.DistributionChannel).ThenBy(x => x.Division).ThenBy(x => x.SalesOffice).ToListAsync(ct);
    }

    // ------------------------------------------------------------ Shipping Condition (TVSB)

    /// <summary>Đồng bộ Shipping Condition từ SAP (TVSB / TVSBT).</summary>
    [HttpPost("shipping-conditions/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncShippingConditions(CancellationToken ct) => SyncOne(MasterDataObject.ShippingCondition, ct);

    /// <summary>Shipping Condition trong DB local.</summary>
    [HttpGet("shipping-conditions")]
    public Task<List<ShippingCondition>> GetShippingConditions(CancellationToken ct) =>
        _repo.Query<ShippingCondition>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Sales Org (TVKO)

    /// <summary>Đồng bộ Sales Organization từ SAP (TVKO / TVKOT).</summary>
    [HttpPost("sales-orgs/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncSalesOrgs(CancellationToken ct) => SyncOne(MasterDataObject.SalesOrg, ct);

    /// <summary>Sales Organization trong DB local.</summary>
    [HttpGet("sales-orgs")]
    public Task<List<SalesOrganization>> GetSalesOrgs(CancellationToken ct) =>
        _repo.Query<SalesOrganization>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Plant (T001W)

    /// <summary>Đồng bộ Plant từ SAP (T001W) — chỉ plant có quốc gia VN (cấu hình Sap:PlantCountries).</summary>
    [HttpPost("plants/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncPlants(CancellationToken ct) => SyncOne(MasterDataObject.Plant, ct);

    /// <summary>Plant trong DB local, lọc theo sales org (T001W-VKORG).</summary>
    [HttpGet("plants")]
    public Task<List<Plant>> GetPlants([FromQuery] string? salesOrg, CancellationToken ct)
    {
        var q = _repo.Query<Plant>();
        if (!string.IsNullOrWhiteSpace(salesOrg)) q = q.Where(x => x.SalesOrg == salesOrg.Trim().ToUpper());
        return q.OrderBy(x => x.Code).ToListAsync(ct);
    }

    // ------------------------------------------------------------ Payment Method (T042Z)

    /// <summary>Đồng bộ Payment Method từ SAP (T042Z).</summary>
    [HttpPost("payment-methods/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncPaymentMethods(CancellationToken ct) => SyncOne(MasterDataObject.PaymentMethod, ct);

    /// <summary>Payment Method trong DB local, lọc theo quốc gia (vd. VN).</summary>
    [HttpGet("payment-methods")]
    public Task<List<PaymentMethod>> GetPaymentMethods([FromQuery] string? country, CancellationToken ct)
    {
        var q = _repo.Query<PaymentMethod>();
        if (!string.IsNullOrWhiteSpace(country)) q = q.Where(x => x.Country == country.Trim().ToUpper());
        return q.OrderBy(x => x.Country).ThenBy(x => x.Code).ToListAsync(ct);
    }

    // ------------------------------------------------------------ Vehicle Type (T173)

    /// <summary>Đồng bộ Vehicle Type từ SAP (shipping type T173 / T173T).</summary>
    [HttpPost("vehicle-types/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncVehicleTypes(CancellationToken ct) => SyncOne(MasterDataObject.VehicleType, ct);

    /// <summary>Vehicle Type trong DB local.</summary>
    [HttpGet("vehicle-types")]
    public Task<List<VehicleType>> GetVehicleTypes(CancellationToken ct) =>
        _repo.Query<VehicleType>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Customer Group 3 (TVV3)

    /// <summary>Đồng bộ Customer Group 3 từ SAP (TVV3, tên từ TVV3T).</summary>
    [HttpPost("customer-groups-3/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncCustomerGroups3(CancellationToken ct) => SyncOne(MasterDataObject.CustomerGroup3, ct);

    /// <summary>Customer Group 3 trong DB local.</summary>
    [HttpGet("customer-groups-3")]
    public Task<List<CustomerGroup3>> GetCustomerGroups3(CancellationToken ct) =>
        _repo.Query<CustomerGroup3>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Material (MARA + MAKT)

    /// <summary>
    /// Đồng bộ danh mục vật tư từ SAP — chỉ loại vật tư ZFNP (Finished Goods, cấu hình Sap:MaterialTypes):
    /// MARA + tên EN/VI từ MAKT + đơn vị tính T006A.
    /// </summary>
    [HttpPost("materials/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncMaterials(CancellationToken ct) => SyncOne(MasterDataObject.Material, ct);

    /// <summary>
    /// Vật tư trong DB local. search: theo mã / tên EN / tên VI. Mặc định bỏ vật tư bị đánh dấu xóa (includeDeleted=true để lấy cả).
    /// </summary>
    [HttpGet("materials")]
    public async Task<PagedResult<Material>> GetMaterials(
        [FromQuery] string? search, [FromQuery] string? materialType, [FromQuery] string? materialGroup, [FromQuery] string? division,
        [FromQuery] bool includeDeleted = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var q = _repo.Query<Material>();
        if (!includeDeleted) q = q.Where(x => !x.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.MaterialCode.Contains(s) || x.NameEn!.Contains(s) || x.NameVi!.Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(materialType)) q = q.Where(x => x.MaterialType == materialType.Trim().ToUpper());
        if (!string.IsNullOrWhiteSpace(materialGroup)) q = q.Where(x => x.MaterialGroup == materialGroup.Trim().ToUpper());
        if (!string.IsNullOrWhiteSpace(division)) q = q.Where(x => x.Division == division.Trim());
        return await Page(q.OrderBy(x => x.MaterialNumber), page, pageSize, ct);
    }

    /// <summary>1 vật tư trong DB local — nhận mã hiển thị (100016) hoặc mã SAP đủ 18 ký tự.</summary>
    [HttpGet("materials/{material}")]
    [ProducesResponseType<Material>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Material>> GetMaterial(string material, CancellationToken ct)
    {
        var m = material.Trim().ToUpperInvariant();
        return await _repo.Query<Material>().FirstOrDefaultAsync(x => x.MaterialCode == m || x.MaterialNumber == m, ct) is { } r
            ? r : NotFound();
    }

    // ------------------------------------------------------------ Material group (T023)

    /// <summary>Đồng bộ Material group từ SAP (T023 / T023T).</summary>
    [HttpPost("material-groups/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncMaterialGroups(CancellationToken ct) => SyncOne(MasterDataObject.MaterialGroup, ct);

    /// <summary>Material group trong DB local.</summary>
    [HttpGet("material-groups")]
    public Task<List<MaterialGroup>> GetMaterialGroups(CancellationToken ct) =>
        _repo.Query<MaterialGroup>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Material type (T134)

    /// <summary>Đồng bộ Material type từ SAP (T134 / T134T).</summary>
    [HttpPost("material-types/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncMaterialTypes(CancellationToken ct) => SyncOne(MasterDataObject.MaterialType, ct);

    /// <summary>Material type trong DB local.</summary>
    [HttpGet("material-types")]
    public Task<List<MaterialType>> GetMaterialTypes(CancellationToken ct) =>
        _repo.Query<MaterialType>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Division (TSPA)

    /// <summary>Đồng bộ Division từ SAP (TSPA / TSPAT).</summary>
    [HttpPost("divisions/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncDivisions(CancellationToken ct) => SyncOne(MasterDataObject.Division, ct);

    /// <summary>Division trong DB local.</summary>
    [HttpGet("divisions")]
    public Task<List<Division>> GetDivisions(CancellationToken ct) =>
        _repo.Query<Division>().OrderBy(x => x.Code).ToListAsync(ct);

    // ------------------------------------------------------------ Tài khoản GL (BAPI_GL_ACC_GETLIST)

    /// <summary>
    /// Đồng bộ danh mục tài khoản GL từ SAP (BAPI_GL_ACC_GETLIST) — chỉ COA 1000, cho các company code
    /// thuộc CO area G100 (cấu hình Sap:ChartOfAccounts / Sap:ControllingArea).
    /// </summary>
    [HttpPost("gl-accounts/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncGlAccounts(CancellationToken ct) => SyncOne(MasterDataObject.GlAccount, ct);

    /// <summary>Tài khoản GL trong DB local, lọc theo company code; search theo số tài khoản / tên.</summary>
    [HttpGet("gl-accounts")]
    public async Task<PagedResult<GlAccount>> GetGlAccounts(
        [FromQuery] string? companyCode, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var q = _repo.Query<GlAccount>();
        if (!string.IsNullOrWhiteSpace(companyCode)) q = q.Where(x => x.CompanyCode == companyCode.Trim().ToUpper());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.AccountCode.Contains(s) || x.ShortText!.Contains(s) || x.LongText!.Contains(s));
        }
        return await Page(q.OrderBy(x => x.CompanyCode).ThenBy(x => x.AccountNumber), page, pageSize, ct);
    }

    // ------------------------------------------------------------ Cost center (BAPI_COSTCENTER_GETLIST1)

    /// <summary>Đồng bộ danh mục cost center từ SAP (BAPI_COSTCENTER_GETLIST1) — chỉ CO area G100 (Sap:ControllingArea).</summary>
    [HttpPost("cost-centers/sync")]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<MasterDataSyncResult>(StatusCodes.Status502BadGateway)]
    public Task<IActionResult> SyncCostCenters(CancellationToken ct) => SyncOne(MasterDataObject.CostCenter, ct);

    /// <summary>Cost center trong DB local; search theo mã / tên / mô tả.</summary>
    [HttpGet("cost-centers")]
    public async Task<PagedResult<CostCenter>> GetCostCenters(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var q = _repo.Query<CostCenter>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.Code.Contains(s) || x.Name!.Contains(s) || x.Description!.Contains(s));
        }
        return await Page(q.OrderBy(x => x.ControllingArea).ThenBy(x => x.Code), page, pageSize, ct);
    }

    // ------------------------------------------------------------ Tất cả

    /// <summary>Đồng bộ tất cả bảng master data (cũng chạy định kỳ qua Hangfire job sync-master-data).</summary>
    [HttpPost("sync-all")]
    [ProducesResponseType<IReadOnlyList<MasterDataSyncResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<IReadOnlyList<MasterDataSyncResult>>(StatusCodes.Status207MultiStatus)]
    public async Task<IActionResult> SyncAll(CancellationToken ct)
    {
        var results = await _sync.SyncAsync(null, "Api", ct);
        return StatusCode(results.All(r => r.Success) ? StatusCodes.Status200OK : StatusCodes.Status207MultiStatus, results);
    }

    /// <summary>Lịch sử đồng bộ (mới nhất trước).</summary>
    [HttpGet("sync-runs")]
    public Task<List<MasterDataSyncRun>> GetSyncRuns([FromQuery] MasterDataObject? @object, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var q = _repo.Query<MasterDataSyncRun>();
        if (@object is { } o) q = q.Where(x => x.Object == o);
        return q.OrderByDescending(x => x.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    // ------------------------------------------------------------ helpers

    private async Task<IActionResult> SyncOne(MasterDataObject obj, CancellationToken ct)
    {
        var result = (await _sync.SyncAsync(new[] { obj }, "Api", ct)).Single();
        return StatusCode(result.Success ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway, result);
    }

    private static async Task<PagedResult<T>> Page<T>(IQueryable<T> q, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<T>(items, page, pageSize, total);
    }

    private static string PadCustomer(string customer)
    {
        var c = customer.Trim().ToUpperInvariant();
        return c.Length < 10 && c.All(char.IsDigit) ? c.PadLeft(10, '0') : c;
    }
}
