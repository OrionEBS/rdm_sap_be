using Hangfire;
using Rdm.Application.Abstractions;

namespace Rdm.Infrastructure.Jobs;

/// <summary>
/// Job Hangfire chạy 1 posting xử lý nền (bảng giá hàng loạt). KHÔNG tự retry: lời gọi ghi SAP không được gửi lại tự động.
/// Server tắt giữa chừng → Hangfire đưa job vào lại hàng đợi; PostingService nhận ra job đã claim và chuyển Unknown
/// (tạo / sửa) hoặc chạy lại (hủy / duyệt / gỡ duyệt — idempotent).
/// </summary>
public sealed class PostingJob
{
    private readonly IPostingService _postings;

    public PostingJob(IPostingService postings)
    {
        _postings = postings;
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [JobDisplayName("Posting {0}")]
    public Task RunAsync(Guid postingId) => _postings.RunJobAsync(postingId, CancellationToken.None);
}

/// <summary>Hàng đợi xử lý nền bằng Hangfire.</summary>
public sealed class HangfirePostingQueue : IBackgroundPostingQueue
{
    private readonly IBackgroundJobClient _jobs;

    public HangfirePostingQueue(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public void Enqueue(Guid postingId) => _jobs.Enqueue<PostingJob>(j => j.RunAsync(postingId));
}
