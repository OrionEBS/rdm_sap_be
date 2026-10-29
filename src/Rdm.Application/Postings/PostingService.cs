using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rdm.Application.Abstractions;
using Rdm.Application.Requests;

namespace Rdm.Application.Postings;

/// <summary>
/// Điều phối mọi lời gọi ghi SAP:
///   1. Giữ chỗ requestId (bản ghi Pending, unique theo Operation + RequestId).
///   2. Gọi SAP đúng 1 lần — KHÔNG retry lời gọi ghi (rủi ro chứng từ trùng).
///   3. Lưu kết quả. Gửi lại requestId đã Succeeded → trả kết quả cũ; Failed → cho gửi lại;
///      Pending/Unknown → 409 vì không chắc SAP đã tạo chứng từ hay chưa.
/// </summary>
public sealed class PostingService : IPostingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Chu kỳ ghi tiến độ job xuống DB.</summary>
    internal static TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(3);

    private readonly ISapPostingRepository _repository;
    private readonly ISapClient _sap;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostingService> _logger;
    private readonly IBackgroundPostingQueue? _queue;

    public PostingService(ISapPostingRepository repository, ISapClient sap, TimeProvider clock, ILogger<PostingService> logger,
        IBackgroundPostingQueue? queue = null)
    {
        _repository = repository;
        _sap = sap;
        _clock = clock;
        _logger = logger;
        _queue = queue;
    }

    public Task<PostingResponse> CreateFb01Async(Fb01Request request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.Fb01, request, _sap.CreateFb01Async, ct);

    public Task<PostingResponse> CreateFb75Async(Fb75Request request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.Fb75, request, _sap.CreateFb75Async, ct);

    public Task<PostingResponse> ReverseAccDocumentAsync(AccDocumentReversalRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.AccDocumentReversal, request, _sap.ReverseAccDocumentAsync, ct);

    public Task<PostingResponse> CreateVa01Async(Va01Request request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.Va01, request, _sap.CreateVa01Async, ct);

    public Task<PostingResponse> CreateVbn1Async(Vbn1Request request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.Vbn1, request, _sap.CreateVbn1Async, ct);

    public Task<PostingResponse> CreateVa41Async(Va41Request request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.Va41, request, _sap.CreateVa41Async, ct);

    public Task<PostingResponse> UpdateMaterialTaxClassAsync(MaterialTaxClassRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.MaterialTaxClass, request, _sap.UpdateMaterialTaxClassAsync, ct);

    public Task<PostingResponse> PostAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.AccDocumentPost, request, _sap.PostAccDocumentAsync, ct);

    public Task<PostingResponse> CreateContractAsync(ContractCreateRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.ContractCreate, request, _sap.CreateContractAsync, ct);

    public Task<PostingResponse> ChangeContractAsync(ContractChangeRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.ContractChange, request, _sap.ChangeContractAsync, ct);

    public Task<PostingResponse> ApproveContractAsync(ContractApproveRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.ContractApprove, request, _sap.ApproveContractAsync, ct);

    public Task<PostingResponse> CreatePricesAsync(PriceCreateRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceCreate, request, _sap.CreatePricesAsync, ct);

    public Task<PostingResponse> ChangePricesAsync(PriceChangeRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceChange, request, _sap.ChangePricesAsync, ct);

    public Task<PostingResponse> ApprovePricesAsync(PriceApproveRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceApprove, request, _sap.ApprovePricesAsync, ct);

    public Task<PostingResponse> CopyPricesWithVatAsync(PriceCopyWithVatRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceCopyWithVat, request, _sap.CopyPricesWithVatAsync, ct);

    public Task<PostingResponse> BapiCreatePricesAsync(PriceBapiCreateRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceBapiCreate, request, _sap.BapiCreatePricesAsync, ct);

    public Task<PostingResponse> BapiChangePricesAsync(PriceBapiChangeRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceBapiChange, request, _sap.BapiChangePricesAsync, ct);

    public Task<PostingResponse> BapiInvalidatePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceBapiInvalidate, request, _sap.BapiInvalidatePricesAsync, ct);

    public Task<PostingResponse> BapiApprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceBapiApprove, request, _sap.BapiApprovePricesAsync, ct);

    public Task<PostingResponse> BapiUnapprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) =>
        ExecuteAsync(SapOperation.PriceBapiUnapprove, request, _sap.BapiUnapprovePricesAsync, ct);

    private async Task<PostingResponse> ExecuteAsync<TRequest>(
        SapOperation op,
        TRequest request,
        Func<TRequest, CancellationToken, Task<SapCallResult>> call,
        CancellationToken ct)
        where TRequest : IPostingRequest
    {
        var requestId = request.RequestId.Trim();
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var hash = Sha256(json);

        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Operation"] = op.ToString(),
            ["RequestId"] = requestId,
            ["BapiName"] = op.FunctionName()
        });

        var (posting, replayed) = await ReserveAsync(op, requestId, json, hash, ct);
        if (replayed)
        {
            _logger.LogInformation("{Operation} {RequestId} đã thành công trước đó (doc {DocumentNumber}) — trả lại kết quả cũ",
                op, requestId, posting.DocumentNumber);
            return PostingResponse.From(posting, replayed: true);
        }

        // Từ đây không dùng ct của HTTP: client ngắt kết nối giữa chừng không được để bản ghi kẹt ở Pending.
        var sw = Stopwatch.StartNew();
        SapCallResult result;
        // Nhật ký gọi SAP: mọi lời gọi RFC của posting này mang requestId / posting id (đối soát với EU).
        using var sapContext = SapCallContext.BeginOperation(op.ToString(), requestId, posting.Id);
        try
        {
            result = await call(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} {RequestId}: lỗi không lường trước khi gọi SAP", op, requestId);
            result = SapCallResult.Unknown($"Lỗi không lường trước khi gọi SAP: {ex.Message}");
        }
        sw.Stop();

        posting.Complete(result, _clock.GetUtcNow().UtcDateTime, (int)sw.ElapsedMilliseconds);
        await _repository.SaveResultAsync(posting, CancellationToken.None);

        _logger.Log(result.Status == PostingStatus.Succeeded ? LogLevel.Information : LogLevel.Warning,
            "{Operation} {RequestId} → {Status} doc={DocumentNumber} error={ErrorCode} {ErrorMessage} ({DurationMs} ms)",
            op, requestId, result.Status, posting.DocumentNumber, result.ErrorCode, result.ErrorMessage, sw.ElapsedMilliseconds);

        return PostingResponse.From(posting);
    }

    // ------------------------------------------------------------------ xử lý nền (bảng giá hàng loạt)

    /// <summary>Thao tác được phép chạy nền + kiểu request + lời gọi SAP tương ứng.</summary>
    private (Type RequestType, Func<IPostingRequest, CancellationToken, Task<SapCallResult>> Call, bool Idempotent) JobSpec(SapOperation op) =>
        op switch
        {
            SapOperation.PriceBapiCreate => (typeof(PriceBapiCreateRequest), (r, ct) => _sap.BapiCreatePricesAsync((PriceBapiCreateRequest)r, ct), false),
            SapOperation.PriceBapiChange => (typeof(PriceBapiChangeRequest), (r, ct) => _sap.BapiChangePricesAsync((PriceBapiChangeRequest)r, ct), false),
            // Hủy / duyệt / gỡ duyệt: bỏ qua record đã ở trạng thái đích → chạy lại an toàn.
            SapOperation.PriceBapiInvalidate => (typeof(PriceBapiRecordsRequest), (r, ct) => _sap.BapiInvalidatePricesAsync((PriceBapiRecordsRequest)r, ct), true),
            SapOperation.PriceBapiApprove => (typeof(PriceBapiRecordsRequest), (r, ct) => _sap.BapiApprovePricesAsync((PriceBapiRecordsRequest)r, ct), true),
            SapOperation.PriceBapiUnapprove => (typeof(PriceBapiRecordsRequest), (r, ct) => _sap.BapiUnapprovePricesAsync((PriceBapiRecordsRequest)r, ct), true),
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Thao tác không hỗ trợ xử lý nền.")
        };

    public async Task<PostingResponse> SubmitPriceBapiJobAsync(SapOperation op, IPostingRequest request, CancellationToken ct)
    {
        var spec = JobSpec(op);
        if (!spec.RequestType.IsInstanceOfType(request))
            throw new ArgumentException($"{op} cần request kiểu {spec.RequestType.Name}.", nameof(request));
        if (_queue is null) throw new InvalidOperationException("Chưa cấu hình hàng đợi xử lý nền (IBackgroundPostingQueue).");

        var requestId = request.RequestId.Trim();
        var json = JsonSerializer.Serialize(request, spec.RequestType, JsonOptions);
        var hash = Sha256(json);

        SapPosting posting;
        bool replayed;
        try
        {
            (posting, replayed) = await ReserveAsync(op, requestId, json, hash, ct, backgroundJob: true);
        }
        catch (PostingConflictException ex) when (ex.Existing is { Status: PostingStatus.Pending, IsBackgroundJob: true } running
                                                  && running.RequestHash == hash)
        {
            // Gửi lại đúng job đang chạy → trả trạng thái hiện tại thay vì 409.
            return PostingResponse.From(running);
        }
        if (replayed) return PostingResponse.From(posting, replayed: true);

        try
        {
            _queue.Enqueue(posting.Id);
            _logger.LogInformation("{Operation} {RequestId}: đã đưa vào hàng đợi xử lý nền (posting {PostingId})", op, requestId, posting.Id);
        }
        catch (Exception ex)
        {
            // Chưa gửi gì sang SAP → Failed, gửi lại an toàn.
            _logger.LogError(ex, "{Operation} {RequestId}: không đưa được vào hàng đợi", op, requestId);
            posting.Complete(SapCallResult.Failed(SapErrorCodes.Internal, "Không đưa được job vào hàng đợi xử lý nền — chưa ghi SAP, gửi lại an toàn."),
                _clock.GetUtcNow().UtcDateTime, 0);
            await _repository.SaveResultAsync(posting, CancellationToken.None);
        }
        return PostingResponse.From(posting);
    }

    public async Task<PostingResponse?> FindAsync(SapOperation op, string requestId, CancellationToken ct) =>
        await _repository.FindAsync(op, requestId.Trim(), ct) is { } p ? PostingResponse.From(p) : null;

    public async Task RunJobAsync(Guid postingId, CancellationToken ct)
    {
        var posting = await _repository.GetAsync(postingId, ct);
        if (posting is null || posting.Status != PostingStatus.Pending)
        {
            _logger.LogWarning("Job posting {PostingId}: không còn ở trạng thái Pending — bỏ qua", postingId);
            return;
        }

        var op = posting.Operation;
        var spec = JobSpec(op);
        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Operation"] = op.ToString(), ["RequestId"] = posting.RequestId, ["BapiName"] = op.FunctionName()
        });

        if (!await _repository.TryClaimJobAsync(posting.Id, _clock.GetUtcNow().UtcDateTime, CancellationToken.None) && !spec.Idempotent)
        {
            // Lần chạy trước bị gián đoạn (server khởi động lại) khi có thể đã ghi một phần → không chạy lại tạo / sửa.
            posting.Complete(SapCallResult.Unknown(
                    "Job xử lý nền bị gián đoạn (server khởi động lại) khi có thể đã ghi một phần lên SAP — " +
                    "kiểm tra bảng giá trên SAP (VK13 / API đọc bảng giá) rồi gửi lại phần còn thiếu bằng requestId mới."),
                _clock.GetUtcNow().UtcDateTime, 0);
            await _repository.SaveResultAsync(posting, CancellationToken.None);
            _logger.LogError("{Operation} {RequestId}: job bị gián đoạn trước đó → Unknown", op, posting.RequestId);
            return;
        }

        var request = (IPostingRequest)JsonSerializer.Deserialize(posting.RequestJson, spec.RequestType, JsonOptions)!;
        using var sapContext = SapCallContext.Begin(new SapCallContext
        {
            Source = "job", Operation = op.ToString(), RequestId = posting.RequestId, PostingId = posting.Id, JobId = posting.Id.ToString("N")
        });
        var progress = new BulkProgress();
        var last = default(ProgressSnapshot);
        var sw = Stopwatch.StartNew();
        SapCallResult result;
        using (BulkProgress.Begin(progress))
        {
            var call = spec.Call(request, CancellationToken.None);
            while (await Task.WhenAny(call, Task.Delay(ProgressInterval, CancellationToken.None)) != call)
                last = await SaveProgressAsync(posting.Id, progress, last);
            try
            {
                result = await call;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Operation} {RequestId}: lỗi không lường trước khi gọi SAP", op, posting.RequestId);
                result = SapCallResult.Unknown($"Lỗi không lường trước khi gọi SAP ({ex.GetType().Name}).");
            }
        }
        sw.Stop();

        posting.SetProgress(progress.Snapshot());
        await SaveProgressAsync(posting.Id, progress, default);
        posting.Complete(result, _clock.GetUtcNow().UtcDateTime, (int)sw.ElapsedMilliseconds);
        await _repository.SaveResultAsync(posting, CancellationToken.None);
        _logger.Log(result.Status == PostingStatus.Succeeded ? LogLevel.Information : LogLevel.Warning,
            "{Operation} {RequestId} (nền) → {Status} {ProgressDone}/{ProgressTotal} error={ErrorCode} ({DurationMs} ms)",
            op, posting.RequestId, result.Status, posting.ProgressDone, posting.ProgressTotal, result.ErrorCode, sw.ElapsedMilliseconds);
    }

    /// <summary>Lưu tiến độ nếu đổi; lỗi ghi tiến độ không làm hỏng job.</summary>
    private async Task<ProgressSnapshot> SaveProgressAsync(Guid id, BulkProgress progress, ProgressSnapshot last)
    {
        var now = progress.Snapshot();
        if (now == last) return last;
        try
        {
            await _repository.UpdateProgressAsync(id, now, CancellationToken.None);
            return now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lưu được tiến độ job {PostingId}", id);
            return last;
        }
    }

    private async Task<(SapPosting Posting, bool Replayed)> ReserveAsync(
        SapOperation op, string requestId, string json, string hash, CancellationToken ct, bool backgroundJob = false)
    {
        // 2 vòng: vòng 2 chỉ xảy ra khi thua race với request song song cùng requestId.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var existing = await _repository.FindAsync(op, requestId, ct);

            if (existing is null)
            {
                var posting = SapPosting.CreatePending(op, requestId, json, hash, now, backgroundJob);
                if (await _repository.TryAddAsync(posting, ct)) return (posting, false);
                continue;
            }

            switch (existing.Status)
            {
                case PostingStatus.Failed:
                    // Lần trước SAP chắc chắn chưa ghi → cho gửi lại, kể cả khi payload đã được sửa.
                    if (await _repository.TryRestartFailedAsync(existing.Id, json, hash, now, ct, backgroundJob))
                    {
                        existing.IsBackgroundJob = backgroundJob;
                        existing.JobClaimedAtUtc = null;
                        existing.ProgressTotal = existing.ProgressDone = existing.ProgressSucceeded = existing.ProgressFailed = null;
                        existing.Status = PostingStatus.Pending;
                        existing.Attempts++;
                        existing.RequestJson = json;
                        existing.RequestHash = hash;
                        existing.StartedAtUtc = now;
                        existing.CompletedAtUtc = null;
                        existing.ErrorCode = null;
                        existing.ErrorMessage = null;
                        existing.ReturnMessagesJson = null;
                        return (existing, false);
                    }
                    continue;

                case PostingStatus.Succeeded when existing.RequestHash == hash:
                    return (existing, true);

                case PostingStatus.Succeeded:
                    throw new PostingConflictException(
                        $"requestId '{requestId}' đã được dùng để tạo chứng từ {existing.DocumentNumber} với payload khác.", existing);

                case PostingStatus.Pending:
                    throw new PostingConflictException(
                        $"requestId '{requestId}' đang được xử lý.", existing);

                default: // Unknown
                    throw new PostingConflictException(
                        $"requestId '{requestId}' ở trạng thái Unknown: không xác định được SAP đã tạo chứng từ hay chưa. " +
                        "Kiểm tra trên SAP trước khi gửi lại bằng requestId mới.", existing);
            }
        }

        throw new PostingConflictException($"requestId '{requestId}' đang được xử lý song song.", null);
    }

    internal static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
