using Rdm.Application.Customers;
using Rdm.Application.MasterData;

namespace Rdm.Application.Abstractions;

/// <summary>Đọc dữ liệu từ SAP (không ghi) — an toàn để retry.</summary>
public interface ISapMasterDataClient
{
    /// <summary>
    /// Đọc KNKK qua RFC_READ_TABLE. customer = null → tất cả khách hàng;
    /// creditControlArea = null → mọi credit control area.
    /// </summary>
    Task<IReadOnlyList<CustomerCredit>> GetCustomerCreditsAsync(string? customer, string? creditControlArea, CancellationToken ct);

    Task<CustomerPartners> ReadCustomerPartnersAsync(CancellationToken ct);
    Task<IReadOnlyList<SalesDistrict>> ReadSalesDistrictsAsync(CancellationToken ct);
    Task<SalesOfficeData> ReadSalesOfficesAsync(CancellationToken ct);
    Task<IReadOnlyList<ShippingCondition>> ReadShippingConditionsAsync(CancellationToken ct);
    Task<IReadOnlyList<SalesOrganization>> ReadSalesOrganizationsAsync(CancellationToken ct);
    Task<IReadOnlyList<Plant>> ReadPlantsAsync(CancellationToken ct);
    Task<IReadOnlyList<PaymentMethod>> ReadPaymentMethodsAsync(CancellationToken ct);
    Task<IReadOnlyList<VehicleType>> ReadVehicleTypesAsync(CancellationToken ct);
    Task<IReadOnlyList<CustomerGroup3>> ReadCustomerGroups3Async(CancellationToken ct);
    Task<IReadOnlyList<Material>> ReadMaterialsAsync(CancellationToken ct);
    Task<IReadOnlyList<MaterialGroup>> ReadMaterialGroupsAsync(CancellationToken ct);
    Task<IReadOnlyList<MaterialType>> ReadMaterialTypesAsync(CancellationToken ct);
    Task<IReadOnlyList<Division>> ReadDivisionsAsync(CancellationToken ct);
    Task<IReadOnlyList<GlAccount>> ReadGlAccountsAsync(CancellationToken ct);
    Task<IReadOnlyList<CostCenter>> ReadCostCentersAsync(CancellationToken ct);
}

/// <summary>Ghi master data xuống DB local.</summary>
public interface IMasterDataRepository
{
    /// <summary>Thay toàn bộ nội dung bảng (DELETE + SqlBulkCopy trong 1 transaction).</summary>
    Task<int> ReplaceAllAsync<T>(IReadOnlyCollection<T> rows, CancellationToken ct) where T : class;
    Task<int> CountAsync<T>(CancellationToken ct) where T : class;
    Task AddSyncRunAsync(MasterDataSyncRun run, CancellationToken ct);
    /// <summary>Truy vấn chỉ đọc.</summary>
    IQueryable<T> Query<T>() where T : class;
}

/// <summary>Lời gọi đọc SAP thất bại. ErrorCode dùng các hằng trong <see cref="Postings.SapErrorCodes"/>.</summary>
public sealed class SapReadException : Exception
{
    public string ErrorCode { get; }
    public string FunctionName { get; }

    public SapReadException(string errorCode, string functionName, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        FunctionName = functionName;
    }
}
