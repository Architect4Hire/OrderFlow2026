using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OrderFlow.Contracts.Messages;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;

namespace OrderFlow.Fulfillment.API.Managers.Business;

/// <summary>How the simulated carrier misbehaves. Each mode drives a different row of the failure matrix.</summary>
public enum CarrierFailureMode
{
    /// <summary>Always accepts. The happy path.</summary>
    None = 0,

    /// <summary>
    /// Fails <see cref="CarrierOptions.TransientFailuresBeforeSuccess"/> times, then succeeds.
    /// Proves the retry policy actually recovers — the order goes through, and the only trace of
    /// the trouble is in the telemetry.
    /// </summary>
    TransientRecovering = 1,

    /// <summary>
    /// Fails transiently, forever. The retries are bounded ([R]1), so they exhaust and this becomes a
    /// HARD failure. Proves that "retryable" does not mean "retried indefinitely".
    /// </summary>
    TransientPersistent = 2,

    /// <summary>
    /// Fails permanently. NOT retried at all ([R]2) — a rejected address does not get better because
    /// you asked four more times, and retrying it just delays the compensation by a few seconds.
    /// </summary>
    Permanent = 3
}

public class CarrierOptions
{
    public const string SectionName = "Carrier";

    public CarrierFailureMode FailureMode { get; set; } = CarrierFailureMode.None;

    /// <summary>How many transient failures precede success in <see cref="CarrierFailureMode.TransientRecovering"/>.</summary>
    public int TransientFailuresBeforeSuccess { get; set; } = 2;
}

/// <summary>Base for every answer the carrier gives that is FINAL. Distinguishing this from a broken circuit is the point.</summary>
public abstract class CarrierException(string message) : Exception(message);

/// <summary>A fault worth retrying: a timeout, a 503, a dropped connection.</summary>
public sealed class TransientCarrierException(string message) : CarrierException(message);

/// <summary>A fault that retrying cannot fix: a rejected address, an unshippable SKU. Fail fast.</summary>
public sealed class PermanentCarrierException(string message) : CarrierException(message);

public interface ICarrierClient
{
    /// <summary>
    /// Hand the shipment to the carrier. Returns a tracking reference, or throws:
    /// <see cref="CarrierException"/> for a final answer (retries exhausted, or permanently rejected),
    /// <see cref="BrokenCircuitException"/> if the breaker is open and we did not even try.
    /// </summary>
    Task<string> DispatchAsync(
        Guid orderId,
        string customerRef,
        IReadOnlyList<OrderLine> lines,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A carrier that isn't. No HTTP, no SDK — the unreliability is simulated in-process so both the
/// recovering and the unrecoverable paths can be demonstrated on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>The policy is tuned for THIS dependency, not lifted from a global default.</b> A carrier API is
/// slow, occasionally flaky, and expensive to hammer: a few retries with short exponential backoff
/// and jitter, then a circuit breaker so a carrier that is genuinely down stops absorbing the entire
/// thread pool. Copy a policy from another dependency and you get numbers that are wrong for both.
/// </para>
/// <para>
/// <b>Only transient faults are retried</b> ([R]2). The retry and the breaker share one predicate:
/// <see cref="TransientCarrierException"/>. A <see cref="PermanentCarrierException"/> matches
/// neither, so it falls straight through the pipeline untouched — no backoff, no delay, no breaker
/// contribution. It is not a fault in the dependency; it is the dependency telling us the answer.
/// </para>
/// <para>
/// <b>The tracking reference is deterministic in the order id.</b> A redelivered DispatchFulfillment
/// re-dispatches, and if the reference were random the saga would record a different one each time.
/// Same reasoning as the payment auth code.
/// </para>
/// </remarks>
public class CarrierClient(
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<CarrierOptions> options,
    ILogger<CarrierClient> logger) : ICarrierClient
{
    public const string PipelineKey = "carrier";

    /// <summary>Per-order attempt count, so TransientRecovering can recover rather than fail forever.</summary>
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    public async Task<string> DispatchAsync(
        Guid orderId,
        string customerRef,
        IReadOnlyList<OrderLine> lines,
        CancellationToken cancellationToken = default)
    {
        var pipeline = pipelineProvider.GetPipeline(PipelineKey);

        return await pipeline.ExecuteAsync(
            async (state, token) => await CallCarrierAsync(state.orderId, state.lines.Count, token),
            (orderId, lines),
            cancellationToken);
    }

    /// <summary>The "network call". Everything above this line is real; everything below it is theatre.</summary>
    private async Task<string> CallCarrierAsync(Guid orderId, int lineCount, CancellationToken cancellationToken)
    {
        // A real call would take time, and a policy that never waits is a policy that was never tested.
        await Task.Delay(25, cancellationToken);

        var attempt = _attempts.AddOrUpdate(orderId, 1, (_, current) => current + 1);
        var settings = options.Value;

        switch (settings.FailureMode)
        {
            case CarrierFailureMode.Permanent:
                throw new PermanentCarrierException(
                    $"Carrier permanently rejected order {orderId:N}: the address is unserviceable.");

            case CarrierFailureMode.TransientPersistent:
                throw new TransientCarrierException(
                    $"Carrier is unavailable (attempt {attempt}).");

            case CarrierFailureMode.TransientRecovering when attempt <= settings.TransientFailuresBeforeSuccess:
                throw new TransientCarrierException(
                    $"Carrier timed out (attempt {attempt} of {settings.TransientFailuresBeforeSuccess + 1}).");
        }

        var trackingRef = TrackingRefFor(orderId);

        logger.LogInformation(
            "Carrier accepted order {OrderId} ({LineCount} line(s)) as {TrackingRef} on attempt {Attempt}.",
            orderId, lineCount, trackingRef, attempt);

        return trackingRef;
    }

    private static string TrackingRefFor(Guid orderId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(orderId.ToString("N")));

        return $"TRK-{Convert.ToHexString(hash)[..10]}";
    }
}

public static class CarrierClientExtensions
{
    public static IHostApplicationBuilder AddCarrierClient(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<CarrierOptions>(builder.Configuration.GetSection(CarrierOptions.SectionName));

        builder.Services.AddResiliencePipeline(CarrierClient.PipelineKey, pipeline =>
        {
            // Retry is registered FIRST, so it sits OUTSIDE the breaker. A BrokenCircuitException
            // therefore travels straight up through the retry (which does not handle it) to the
            // caller — which is what we want: an open circuit means "do not ask again yet", and
            // retrying against an open breaker is just a busy-wait.
            pipeline.AddRetry(new RetryStrategyOptions
            {
                // [R]2 — transient only. A permanent rejection is not matched and fails fast.
                ShouldHandle = new PredicateBuilder().Handle<TransientCarrierException>(),

                // [R]1 — bounded. Four attempts in total, then the caller gets the final failure.
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });

            pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TransientCarrierException>(),
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // Singleton: the attempt counter has to survive across messages for TransientRecovering to
        // mean anything, and the pipeline is stateful (the breaker's window is shared state).
        builder.Services.AddSingleton<ICarrierClient, CarrierClient>();

        return builder;
    }
}
