using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Rdm.Application.Abstractions;
using Rdm.Application.Postings;
using Rdm.Application.Requests;

namespace Rdm.Tests;

public class PostingServiceTests
{
    private readonly FakeRepository _repo = new();
    private readonly FakeSap _sap = new();
    private PostingService Service => new(_repo, _sap, TimeProvider.System, NullLogger<PostingService>.Instance);

    private static SapCallResult Ok(string belnr = "0100000123") =>
        SapCallResult.Success(new SapDocumentRef(belnr, "VN10", "2026", "BKPFF", belnr + "VN102026"), Array.Empty<SapReturnMessage>());

    [Fact]
    public async Task Success_is_persisted_with_document_number()
    {
        _sap.Next = Ok();

        var r = await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default);

        r.Status.Should().Be(PostingStatus.Succeeded);
        r.DocumentNumber.Should().Be("0100000123");
        r.Replayed.Should().BeFalse();
        _repo.Items.Single().Status.Should().Be(PostingStatus.Succeeded);
    }

    [Fact]
    public async Task Same_request_after_success_is_replayed_without_calling_sap_again()
    {
        _sap.Next = Ok();
        await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default);

        var second = await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default);

        second.Replayed.Should().BeTrue();
        second.DocumentNumber.Should().Be("0100000123");
        _sap.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Reusing_request_id_with_different_payload_after_success_is_a_conflict()
    {
        _sap.Next = Ok();
        await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default);

        var changed = SapRequestMapperTests.ValidFb01();
        changed.GlLine!.Amount = 999m;
        var act = () => Service.CreateFb01Async(changed, default);

        await act.Should().ThrowAsync<PostingConflictException>();
        _sap.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Failed_request_can_be_resent_with_corrected_payload()
    {
        _sap.Next = SapCallResult.Failed(SapErrorCodes.Business, "Balance in transaction currency");
        var first = await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default);
        first.Status.Should().Be(PostingStatus.Failed);

        _sap.Next = Ok();
        var fixedRequest = SapRequestMapperTests.ValidFb01();
        fixedRequest.CustomerLine!.Amount = 1_400_000m;
        var second = await Service.CreateFb01Async(fixedRequest, default);

        second.Status.Should().Be(PostingStatus.Succeeded);
        second.Attempts.Should().Be(2);
        second.ErrorMessage.Should().BeNull();
        _repo.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_outcome_blocks_resend_with_same_request_id()
    {
        _sap.Next = SapCallResult.Unknown("connection lost");
        (await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default)).Status.Should().Be(PostingStatus.Unknown);

        var act = () => Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default);

        (await act.Should().ThrowAsync<PostingConflictException>()).Which.Existing!.Status.Should().Be(PostingStatus.Unknown);
        _sap.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Unexpected_exception_from_sap_client_is_recorded_as_unknown()
    {
        _sap.Throw = new InvalidOperationException("boom");

        var r = await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01(), default);

        r.Status.Should().Be(PostingStatus.Unknown);
        r.ErrorMessage.Should().Contain("boom");
    }

    // ------------------------------------------------------------------ xử lý nền (bảng giá hàng loạt)

    private readonly FakeQueue _queue = new();
    private PostingService JobService => new(_repo, _sap, TimeProvider.System, NullLogger<PostingService>.Instance, _queue);

    private static PriceBapiCreateRequest BulkCreate(string id = "BULK-1") => new()
    {
        RequestId = id,
        Items = Enumerable.Range(1, 3).Select(i => new PriceCreateItem { ConditionType = "ZVLP", Amount = i }).ToList()
    };

    [Fact]
    public async Task Bulk_job_is_queued_without_calling_sap_then_run_by_worker()
    {
        _sap.Next = SapCallResult.Success(new SapDocumentRef(null, ObjectType: "KONH"), Array.Empty<SapReturnMessage>());
        _sap.ReportProgress = true;

        var submitted = await JobService.SubmitPriceBapiJobAsync(SapOperation.PriceBapiCreate, BulkCreate(), default);

        submitted.Status.Should().Be(PostingStatus.Pending);
        submitted.IsBackgroundJob.Should().BeTrue();
        _queue.Ids.Should().ContainSingle().Which.Should().Be(submitted.Id);
        _sap.Calls.Should().Be(0);

        await JobService.RunJobAsync(submitted.Id, default);

        var done = _repo.Items.Single();
        done.Status.Should().Be(PostingStatus.Succeeded);
        done.ProgressTotal.Should().Be(3);
        done.ProgressSucceeded.Should().Be(2);
        done.ProgressFailed.Should().Be(1);
        _sap.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Resubmitting_a_running_job_returns_its_state_instead_of_conflict()
    {
        var first = await JobService.SubmitPriceBapiJobAsync(SapOperation.PriceBapiCreate, BulkCreate(), default);

        var again = await JobService.SubmitPriceBapiJobAsync(SapOperation.PriceBapiCreate, BulkCreate(), default);

        again.Id.Should().Be(first.Id);
        again.Status.Should().Be(PostingStatus.Pending);
        _queue.Ids.Should().ContainSingle();
    }

    [Fact]
    public async Task Interrupted_create_job_is_not_rerun_but_marked_unknown()
    {
        var job = await JobService.SubmitPriceBapiJobAsync(SapOperation.PriceBapiCreate, BulkCreate(), default);
        await _repo.TryClaimJobAsync(job.Id, DateTime.UtcNow, default);   // lần chạy trước đã bắt đầu gọi SAP rồi server tắt

        await JobService.RunJobAsync(job.Id, default);

        _repo.Items.Single().Status.Should().Be(PostingStatus.Unknown);
        _sap.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Interrupted_invalidate_job_is_rerun_because_it_is_idempotent()
    {
        _sap.Next = SapCallResult.Success(new SapDocumentRef(null, ObjectType: "KONH"), Array.Empty<SapReturnMessage>());
        var job = await JobService.SubmitPriceBapiJobAsync(SapOperation.PriceBapiInvalidate,
            new PriceBapiRecordsRequest { RequestId = "DEL-1", ConditionRecordNumbers = new() { "0000119650" } }, default);
        await _repo.TryClaimJobAsync(job.Id, DateTime.UtcNow, default);

        await JobService.RunJobAsync(job.Id, default);

        _repo.Items.Single().Status.Should().Be(PostingStatus.Succeeded);
        _sap.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Operations_other_than_bapi_prices_cannot_run_in_background()
    {
        var act = () => JobService.SubmitPriceBapiJobAsync(SapOperation.Fb01, SapRequestMapperTests.ValidFb01(), default);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _queue.Ids.Should().BeEmpty();
    }

    private sealed class FakeQueue : IBackgroundPostingQueue
    {
        public List<Guid> Ids { get; } = new();
        public void Enqueue(Guid postingId) => Ids.Add(postingId);
    }

    [Fact]
    public async Task Same_request_id_on_different_operations_is_independent()
    {
        _sap.Next = Ok();
        await Service.CreateFb01Async(SapRequestMapperTests.ValidFb01("X"), default);
        var r = await Service.CreateFb75Async(new Fb75Request { RequestId = "X" }, default);

        r.Replayed.Should().BeFalse();
        _sap.Calls.Should().Be(2);
    }

    private sealed class FakeSap : ISapClient
    {
        public SapCallResult Next { get; set; } = SapCallResult.Failed(SapErrorCodes.Internal, "not set");
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        /// <summary>Giả lập tầng SAP báo tiến độ (3 record: 2 thành công, 1 lỗi).</summary>
        public bool ReportProgress { get; set; }

        private Task<SapCallResult> Call()
        {
            Calls++;
            if (ReportProgress && BulkProgress.Current is { } p)
            {
                p.SetTotal(3);
                p.Add(2, 1);
            }
            return Throw is not null ? Task.FromException<SapCallResult>(Throw) : Task.FromResult(Next);
        }

        public Task<SapCallResult> CreateFb01Async(Fb01Request request, CancellationToken ct) => Call();
        public Task<SapCallResult> CreateFb75Async(Fb75Request request, CancellationToken ct) => Call();
        public Task<SapCallResult> ReverseAccDocumentAsync(AccDocumentReversalRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> CreateVa01Async(Va01Request request, CancellationToken ct) => Call();
        public Task<SapCallResult> CreateVbn1Async(Vbn1Request request, CancellationToken ct) => Call();
        public Task<SapCallResult> CreateVa41Async(Va41Request request, CancellationToken ct) => Call();
        public Task<SapCallResult> UpdateMaterialTaxClassAsync(MaterialTaxClassRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> PostAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> CheckAccDocumentAsync(AccDocumentPostRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> CreateContractAsync(ContractCreateRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> ChangeContractAsync(ContractChangeRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> ApproveContractAsync(ContractApproveRequest request, CancellationToken ct) => Call();
        public Task<PriceListResponse> GetPricesAsync(PriceQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<SapCallResult> BapiCreatePricesAsync(PriceBapiCreateRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> BapiChangePricesAsync(PriceBapiChangeRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> BapiInvalidatePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> BapiApprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> BapiUnapprovePricesAsync(PriceBapiRecordsRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> CheckPricesAsync(PriceCreateRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> CreatePricesAsync(PriceCreateRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> ChangePricesAsync(PriceChangeRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> ApprovePricesAsync(PriceApproveRequest request, CancellationToken ct) => Call();
        public Task<SapCallResult> CopyPricesWithVatAsync(PriceCopyWithVatRequest request, CancellationToken ct) => Call();
        public Task<SapSystemInfo> PingAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Repository trong bộ nhớ, mô phỏng unique (Operation, RequestId) và update nguyên tử Failed → Pending.</summary>
    private sealed class FakeRepository : ISapPostingRepository
    {
        public List<SapPosting> Items { get; } = new();

        private static SapPosting Copy(SapPosting p) => (SapPosting)typeof(SapPosting)
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(p, null)!;

        public Task<SapPosting?> FindAsync(SapOperation op, string requestId, CancellationToken ct) =>
            Task.FromResult(Items.Where(x => x.Operation == op && x.RequestId == requestId).Select(Copy).FirstOrDefault());

        public Task<SapPosting?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Items.Where(x => x.Id == id).Select(Copy).FirstOrDefault());

        public Task<bool> TryAddAsync(SapPosting posting, CancellationToken ct)
        {
            if (Items.Any(x => x.Operation == posting.Operation && x.RequestId == posting.RequestId)) return Task.FromResult(false);
            Items.Add(Copy(posting));
            return Task.FromResult(true);
        }

        public Task<bool> TryRestartFailedAsync(Guid id, string requestJson, string requestHash, DateTime nowUtc, CancellationToken ct,
            bool backgroundJob = false)
        {
            var p = Items.SingleOrDefault(x => x.Id == id && x.Status == PostingStatus.Failed);
            if (p is null) return Task.FromResult(false);
            p.Status = PostingStatus.Pending;
            p.Attempts++;
            p.RequestJson = requestJson;
            p.RequestHash = requestHash;
            p.IsBackgroundJob = backgroundJob;
            p.JobClaimedAtUtc = null;
            return Task.FromResult(true);
        }

        public Task<bool> TryClaimJobAsync(Guid id, DateTime nowUtc, CancellationToken ct)
        {
            var p = Items.SingleOrDefault(x => x.Id == id && x.Status == PostingStatus.Pending && x.JobClaimedAtUtc is null);
            if (p is null) return Task.FromResult(false);
            p.JobClaimedAtUtc = nowUtc;
            return Task.FromResult(true);
        }

        public List<ProgressSnapshot> ProgressUpdates { get; } = new();

        public Task UpdateProgressAsync(Guid id, ProgressSnapshot progress, CancellationToken ct)
        {
            ProgressUpdates.Add(progress);
            var p = Items.Single(x => x.Id == id);
            p.SetProgress(progress);
            return Task.CompletedTask;
        }

        public Task SaveResultAsync(SapPosting posting, CancellationToken ct)
        {
            Items.RemoveAll(x => x.Id == posting.Id);
            Items.Add(Copy(posting));
            return Task.CompletedTask;
        }

        public Task<PagedResult<SapPosting>> SearchAsync(PostingQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
