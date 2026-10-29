using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rdm.Application.Abstractions;

namespace Rdm.Application.MasterData;

public sealed record MasterDataSyncResult(
    MasterDataObject Object, bool Success, int RowCount, int DurationMs, string? Error);

/// <summary>Đang có 1 lượt đồng bộ khác chạy → HTTP 409.</summary>
public sealed class MasterDataSyncInProgressException() : Exception("Đang có lượt đồng bộ master data khác chạy. Thử lại sau.");

public interface IMasterDataSyncService
{
    /// <summary>objects rỗng / null = đồng bộ tất cả.</summary>
    Task<IReadOnlyList<MasterDataSyncResult>> SyncAsync(IReadOnlyCollection<MasterDataObject>? objects, string trigger, CancellationToken ct);
}

/// <summary>
/// Đọc master data từ SAP và thay toàn bộ bảng tương ứng trong DB local.
/// Mỗi nhóm đồng bộ độc lập: 1 nhóm lỗi không làm hỏng nhóm khác, và bảng lỗi giữ nguyên dữ liệu cũ.
/// </summary>
public sealed class MasterDataSyncService : IMasterDataSyncService
{
    // 1 lượt đồng bộ tại 1 thời điểm trong process (API + Hangfire dùng chung).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly ISapMasterDataClient _sap;
    private readonly IMasterDataRepository _repository;
    private readonly TimeProvider _clock;
    private readonly ILogger<MasterDataSyncService> _logger;

    public MasterDataSyncService(ISapMasterDataClient sap, IMasterDataRepository repository, TimeProvider clock,
        ILogger<MasterDataSyncService> logger)
    {
        _sap = sap;
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MasterDataSyncResult>> SyncAsync(
        IReadOnlyCollection<MasterDataObject>? objects, string trigger, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) throw new MasterDataSyncInProgressException();
        try
        {
            var targets = objects is { Count: > 0 } ? objects.Distinct().ToList() : Enum.GetValues<MasterDataObject>().ToList();
            var results = new List<MasterDataSyncResult>(targets.Count);
            CustomerPartners? partners = null; // SoldTo / ShipTo / SoldToShipTo dùng chung 1 lần đọc KNA1/KNVV/KNVP

            foreach (var obj in targets)
            {
                var started = _clock.GetUtcNow().UtcDateTime;
                var sw = Stopwatch.StartNew();
                int rows = 0;
                string? error = null;
                // Nhật ký gọi SAP: gắn lời gọi RFC_READ_TABLE với đối tượng đồng bộ (Api = bấm tay, Schedule = job định kỳ).
                using var _ = SapCallContext.BeginOperation($"MasterDataSync.{obj}",
                    source: string.Equals(trigger, "Api", StringComparison.OrdinalIgnoreCase) ? null : "schedule");
                try
                {
                    rows = obj switch
                    {
                        MasterDataObject.SoldTo => await ReplaceAsync((partners ??= await _sap.ReadCustomerPartnersAsync(ct)).SoldTo, ct),
                        MasterDataObject.ShipTo => await ReplaceAsync((partners ??= await _sap.ReadCustomerPartnersAsync(ct)).ShipTo, ct),
                        MasterDataObject.SoldToShipTo => await ReplaceAsync((partners ??= await _sap.ReadCustomerPartnersAsync(ct)).Relations, ct),
                        MasterDataObject.SalesDistrict => await ReplaceAsync(await _sap.ReadSalesDistrictsAsync(ct), ct),
                        MasterDataObject.SalesOffice => await ReplaceSalesOfficesAsync(ct),
                        MasterDataObject.ShippingCondition => await ReplaceAsync(await _sap.ReadShippingConditionsAsync(ct), ct),
                        MasterDataObject.SalesOrg => await ReplaceAsync(await _sap.ReadSalesOrganizationsAsync(ct), ct),
                        MasterDataObject.Plant => await ReplaceAsync(await _sap.ReadPlantsAsync(ct), ct),
                        MasterDataObject.PaymentMethod => await ReplaceAsync(await _sap.ReadPaymentMethodsAsync(ct), ct),
                        MasterDataObject.VehicleType => await ReplaceAsync(await _sap.ReadVehicleTypesAsync(ct), ct),
                        MasterDataObject.CustomerGroup3 => await ReplaceAsync(await _sap.ReadCustomerGroups3Async(ct), ct),
                        MasterDataObject.Material => await ReplaceAsync(await _sap.ReadMaterialsAsync(ct), ct),
                        MasterDataObject.MaterialGroup => await ReplaceAsync(await _sap.ReadMaterialGroupsAsync(ct), ct),
                        MasterDataObject.MaterialType => await ReplaceAsync(await _sap.ReadMaterialTypesAsync(ct), ct),
                        MasterDataObject.Division => await ReplaceAsync(await _sap.ReadDivisionsAsync(ct), ct),
                        MasterDataObject.GlAccount => await ReplaceAsync(await _sap.ReadGlAccountsAsync(ct), ct),
                        MasterDataObject.CostCenter => await ReplaceAsync(await _sap.ReadCostCentersAsync(ct), ct),
                        _ => throw new ArgumentOutOfRangeException(nameof(objects), obj, null)
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                    _logger.LogError(ex, "Đồng bộ master data {Object} lỗi", obj);
                }
                sw.Stop();

                var result = new MasterDataSyncResult(obj, error is null, rows, (int)sw.ElapsedMilliseconds, error);
                results.Add(result);
                _logger.LogInformation("Đồng bộ {Object}: {Success} {Rows} dòng ({DurationMs} ms)", obj, result.Success, rows, result.DurationMs);

                await _repository.AddSyncRunAsync(new MasterDataSyncRun
                {
                    Object = obj,
                    Trigger = trigger,
                    StartedAtUtc = started,
                    CompletedAtUtc = _clock.GetUtcNow().UtcDateTime,
                    DurationMs = result.DurationMs,
                    Success = result.Success,
                    RowCount = rows,
                    Error = error is { Length: > 2000 } ? error[..2000] : error
                }, CancellationToken.None);
            }
            return results;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<int> ReplaceSalesOfficesAsync(CancellationToken ct)
    {
        var data = await _sap.ReadSalesOfficesAsync(ct);
        var offices = await ReplaceAsync(data.Offices, ct);
        await ReplaceAsync(data.Assignments, ct, allowEmpty: true);
        return offices;
    }

    private async Task<int> ReplaceAsync<T>(IReadOnlyList<T> rows, CancellationToken ct, bool allowEmpty = false)
        where T : class, IMasterDataRow
    {
        // SAP trả 0 dòng trong khi local đang có dữ liệu → nghi lỗi phía SAP/quyền, không xóa trắng bảng.
        if (rows.Count == 0 && !allowEmpty && await _repository.CountAsync<T>(ct) > 0)
            throw new InvalidOperationException($"SAP trả 0 dòng cho {typeof(T).Name} — giữ nguyên dữ liệu cũ.");

        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var r in rows) r.SyncedAtUtc = now;
        return await _repository.ReplaceAllAsync(rows.ToList(), ct);
    }
}
