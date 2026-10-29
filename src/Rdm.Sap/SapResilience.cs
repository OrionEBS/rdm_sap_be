using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SAP.Middleware.Connector;

namespace Rdm.Sap;

/// <summary>
/// Polly pipeline CHỈ bọc thao tác an toàn để retry: ping trước khi ghi (chưa có gì gửi sang SAP) và các lời gọi đọc (RFC_READ_TABLE).
/// Lời gọi ghi (Invoke) không bao giờ retry: rủi ro tạo chứng từ trùng.
///   - Retry: lỗi mạng / hết connection trong pool, backoff lũy thừa từ 500 ms.
///   - Circuit breaker: ≥50% lỗi kết nối trong 30 s (tối thiểu 4 lần) → fail-fast trong CircuitBreakSeconds.
/// </summary>
public sealed class SapResilience
{
    public ResiliencePipeline Connect { get; }

    public SapResilience(IOptions<SapOptions> options, ILogger<SapResilience> logger)
    {
        var o = options.Value;
        var transient = new PredicateBuilder()
            .Handle<RfcCommunicationException>()
            .Handle<RfcResourceException>();

        var builder = new ResiliencePipelineBuilder();
        if (o.ConnectRetryCount > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = o.ConnectRetryCount,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(500),
                ShouldHandle = transient,
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "Kết nối SAP lỗi, thử lại lần {Attempt} sau {Delay}",
                        args.AttemptNumber + 1, args.RetryDelay);
                    return default;
                }
            });
        }

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(Math.Max(5, o.CircuitBreakSeconds)),
            ShouldHandle = transient,
            OnOpened = args =>
            {
                logger.LogError(args.Outcome.Exception, "Circuit breaker SAP MỞ trong {Duration}", args.BreakDuration);
                return default;
            },
            OnClosed = _ =>
            {
                logger.LogInformation("Circuit breaker SAP đóng lại");
                return default;
            }
        });

        Connect = builder.Build();
    }
}
