using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Application.Abstractions;

/// <summary>Gateway sang SAP. Không throw cho lỗi phía SAP — trả <see cref="SapCallResult"/>.</summary>
public interface ISapClient
{
    Task<SapCallResult> CreateFb01Async(Fb01Request request, CancellationToken ct);
    Task<SapCallResult> CreateFb75Async(Fb75Request request, CancellationToken ct);
    Task<SapCallResult> ReverseAccDocumentAsync(AccDocumentReversalRequest request, CancellationToken ct);
    Task<SapCallResult> CreateVa01Async(Va01Request request, CancellationToken ct);
    Task<SapCallResult> CreateVbn1Async(Vbn1Request request, CancellationToken ct);
    Task<SapCallResult> CreateVa41Async(Va41Request request, CancellationToken ct);
    Task<SapCallResult> UpdateMaterialTaxClassAsync(MaterialTaxClassRequest request, CancellationToken ct);
    Task<SapCallResult> PostAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct);
    /// <summary>BAPI_ACC_DOCUMENT_CHECK — chỉ kiểm tra, không ghi. Succeeded = SAP không báo lỗi.</summary>
    Task<SapCallResult> CheckAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct);
    Task<SapCallResult> CreateContractAsync(ContractCreateRequest request, CancellationToken ct);
    Task<SapCallResult> ChangeContractAsync(ContractChangeRequest request, CancellationToken ct);
    Task<SapCallResult> ApproveContractAsync(ContractApproveRequest request, CancellationToken ct);
    /// <summary>Đọc bảng giá ZVLP / ZVDI / ZMIN / ZVIV (Axxx + KONP).</summary>
    Task<PriceListResponse> GetPricesAsync(PriceQuery query, CancellationToken ct);
    /// <summary>Chỉ kiểm tra dữ liệu + trùng / chồng hiệu lực, không ghi SAP.</summary>
    Task<SapCallResult> CheckPricesAsync(PriceCreateRequest request, CancellationToken ct);
    Task<SapCallResult> CreatePricesAsync(PriceCreateRequest request, CancellationToken ct);
    Task<SapCallResult> ChangePricesAsync(PriceChangeRequest request, CancellationToken ct);
    Task<SapCallResult> ApprovePricesAsync(PriceApproveRequest request, CancellationToken ct);
    Task<SapCallResult> CopyPricesWithVatAsync(PriceCopyWithVatRequest request, CancellationToken ct);

    // Bảng giá qua BAPI_PRICES_CONDITIONS (vỏ RFC Z_ESD016_PRICES_CONDITIONS) — tách riêng để so sánh với phương án BDC.
    Task<SapCallResult> BapiCreatePricesAsync(PriceBapiCreateRequest request, CancellationToken ct);
    Task<SapCallResult> BapiChangePricesAsync(PriceBapiChangeRequest request, CancellationToken ct);
    Task<SapCallResult> BapiInvalidatePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct);
    Task<SapCallResult> BapiApprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct);
    Task<SapCallResult> BapiUnapprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct);
    Task<SapSystemInfo> PingAsync(CancellationToken ct);
}

public sealed record SapSystemInfo(string SystemId, string Client, string Host, string Release, string User, int RoundTripMs);

public interface ISapPostingRepository
{
    Task<SapPosting?> FindAsync(SapOperation op, string requestId, CancellationToken ct);
    Task<SapPosting?> GetAsync(Guid id, CancellationToken ct);
    /// <summary>Insert bản ghi Pending. false nếu (Operation, RequestId) đã tồn tại (race giữa 2 request).</summary>
    Task<bool> TryAddAsync(SapPosting posting, CancellationToken ct);
    /// <summary>Chuyển Failed → Pending một cách nguyên tử. false nếu bản ghi không còn ở trạng thái Failed.</summary>
    Task<bool> TryRestartFailedAsync(Guid id, string requestJson, string requestHash, DateTime nowUtc, CancellationToken ct,
        bool backgroundJob = false);
    Task SaveResultAsync(SapPosting posting, CancellationToken ct);
    Task<PagedResult<SapPosting>> SearchAsync(PostingQuery query, CancellationToken ct);
    /// <summary>Đánh dấu job bắt đầu gọi SAP. false nếu đã được đánh dấu trước đó (lần chạy trước bị gián đoạn).</summary>
    Task<bool> TryClaimJobAsync(Guid id, DateTime nowUtc, CancellationToken ct);
    Task UpdateProgressAsync(Guid id, ProgressSnapshot progress, CancellationToken ct);
}

/// <summary>Hàng đợi xử lý nền cho posting (Hangfire) — tầng Application không phụ thuộc Hangfire.</summary>
public interface IBackgroundPostingQueue
{
    void Enqueue(Guid postingId);
}

public interface IPostingService
{
    Task<PostingResponse> CreateFb01Async(Fb01Request request, CancellationToken ct);
    Task<PostingResponse> CreateFb75Async(Fb75Request request, CancellationToken ct);
    Task<PostingResponse> ReverseAccDocumentAsync(AccDocumentReversalRequest request, CancellationToken ct);
    Task<PostingResponse> CreateVa01Async(Va01Request request, CancellationToken ct);
    Task<PostingResponse> CreateVbn1Async(Vbn1Request request, CancellationToken ct);
    Task<PostingResponse> CreateVa41Async(Va41Request request, CancellationToken ct);
    Task<PostingResponse> UpdateMaterialTaxClassAsync(MaterialTaxClassRequest request, CancellationToken ct);
    Task<PostingResponse> PostAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct);
    Task<PostingResponse> CreateContractAsync(ContractCreateRequest request, CancellationToken ct);
    Task<PostingResponse> ChangeContractAsync(ContractChangeRequest request, CancellationToken ct);
    Task<PostingResponse> ApproveContractAsync(ContractApproveRequest request, CancellationToken ct);
    Task<PostingResponse> CreatePricesAsync(PriceCreateRequest request, CancellationToken ct);
    Task<PostingResponse> ChangePricesAsync(PriceChangeRequest request, CancellationToken ct);
    Task<PostingResponse> ApprovePricesAsync(PriceApproveRequest request, CancellationToken ct);
    Task<PostingResponse> CopyPricesWithVatAsync(PriceCopyWithVatRequest request, CancellationToken ct);
    Task<PostingResponse> BapiCreatePricesAsync(PriceBapiCreateRequest request, CancellationToken ct);
    Task<PostingResponse> BapiChangePricesAsync(PriceBapiChangeRequest request, CancellationToken ct);
    Task<PostingResponse> BapiInvalidatePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct);
    Task<PostingResponse> BapiApprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct);
    Task<PostingResponse> BapiUnapprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct);

    /// <summary>
    /// Bảng giá hàng loạt (BAPI) xử lý nền: giữ chỗ requestId rồi đưa vào hàng đợi, trả ngay (Status Pending).
    /// Gửi lại cùng requestId + payload khi đang chạy → trả trạng thái hiện tại; đã xong → trả kết quả cũ.
    /// </summary>
    Task<PostingResponse> SubmitPriceBapiJobAsync(SapOperation op, IPostingRequest request, CancellationToken ct);
    Task<PostingResponse?> FindAsync(SapOperation op, string requestId, CancellationToken ct);
    /// <summary>Chạy job đã giữ chỗ (gọi từ Hangfire).</summary>
    Task RunJobAsync(Guid postingId, CancellationToken ct);
}
