using Microsoft.Extensions.Options;
using OrderFlow.Contracts.Messages;
using OrderFlow.Fulfillment.API.Managers.Business;
using OrderFlow.Notification.API.Managers.Business;
using OrderFlow.Notification.API.Managers.Data;
using OrderFlow.Notification.API.Managers.Domain;
using OrderFlow.ServiceDefaults.Messaging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;

namespace OrderFlow.UnitTests;

/// <summary>Hands out a real Polly pipeline without a DI container.</summary>
/// <remarks>
/// A REAL pipeline, not a stub — the retry bound and the timeout are the behaviour under test, so
/// faking them away would leave the tests asserting on nothing.
/// </remarks>
public sealed class TestPipelineProvider(string key, ResiliencePipeline pipeline) : ResiliencePipelineProvider<string>
{
    public override bool TryGetPipeline(string pipelineKey, out ResiliencePipeline? result)
    {
        result = pipelineKey == key ? pipeline : null;

        return result is not null;
    }

    public override bool TryGetPipeline<TResult>(string pipelineKey, out ResiliencePipeline<TResult>? result)
    {
        result = null;

        return false;
    }
}

/// <summary>A carrier that answers however the test needs it to.</summary>
public sealed class StubCarrierClient(Func<int, string> behaviour) : ICarrierClient
{
    public int Calls { get; private set; }

    public Task<string> DispatchAsync(
        Guid orderId,
        string customerRef,
        IReadOnlyList<OrderLine> lines,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        return Task.FromResult(behaviour(Calls));
    }
}

public class FulfillmentBusinessTests
{
    private readonly FakeDeadLetterBrowser _deadLetters = new();

    private FulfillmentBusinessManager Create(ICarrierClient carrier) =>
        new(carrier, _deadLetters, Log.For<FulfillmentBusinessManager>());

    [Fact]
    public async Task A_successful_dispatch_returns_the_tracking_reference()
    {
        var business = Create(new StubCarrierClient(_ => "TRK-ABC1234567"));

        var result = await business.DispatchAsync(Guid.NewGuid(), "CUST-1", []);

        Assert.True(result.Dispatched);
        Assert.Equal("TRK-ABC1234567", result.TrackingRef);
    }

    [Fact]
    public async Task A_permanent_carrier_rejection_becomes_a_HARD_FAILURE_not_an_exception()
    {
        var business = Create(new StubCarrierClient(_ => throw new PermanentCarrierException("Address unserviceable.")));

        var result = await business.DispatchAsync(Guid.NewGuid(), "CUST-1", []);

        // The carrier gave a FINAL answer, and the answer was no. That is a business outcome: the
        // consumer publishes FulfillmentFailed, the saga refunds and releases, the order dies cleanly.
        // Letting it throw would dead-letter the command and the saga would never be told ANYTHING —
        // money captured, stock held, order frozen at Paid forever.
        Assert.False(result.Dispatched);
        Assert.Contains("unserviceable", result.FailureReason);
    }

    [Fact]
    public async Task An_exhausted_transient_failure_becomes_a_HARD_FAILURE_too()
    {
        var business = Create(new StubCarrierClient(_ => throw new TransientCarrierException("Carrier unavailable.")));

        var result = await business.DispatchAsync(Guid.NewGuid(), "CUST-1", []);

        Assert.False(result.Dispatched);
    }

    [Fact]
    public async Task A_BROKEN_CIRCUIT_is_NOT_a_hard_failure_and_must_propagate()
    {
        var business = Create(new StubCarrierClient(_ => throw new BrokenCircuitException()));

        // The distinction this service exists to draw. An open breaker means we never SPOKE to the
        // carrier — we have no answer, only the absence of one. Turning that into FulfillmentFailed
        // would kill a perfectly good order, refund a customer who was about to get their parcel, and
        // release stock that did not need releasing, because a dependency was briefly unhealthy.
        // It must escape, so the message is abandoned and retried once the breaker closes.
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => business.DispatchAsync(Guid.NewGuid(), "CUST-1", []));
    }
}

/// <summary>A dead-letter browser that finds nothing. These tests are not about the DLQ.</summary>
public sealed class FakeDeadLetterBrowser : IDeadLetterBrowser
{
    public Task<IReadOnlyList<DeadLetteredMessage>> PeekAsync(DeadLetterSource source, int maxMessages, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeadLetteredMessage>>([]);

    public Task<IReadOnlyList<DeadLetteredMessage>> PeekAllAsync(int maxMessagesPerSource, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeadLetteredMessage>>([]);
}

/// <summary>
/// A notification provider that fails, hangs, or works, on command.
/// </summary>
/// <remarks>
/// <b>The behaviour is handed the CancellationToken, and that is not an incidental detail.</b> Polly's
/// timeout works by cancelling the token — it cannot abandon a Task that ignores one. A provider that
/// blocks without observing cancellation CANNOT be timed out, and the pipeline will sit and wait for
/// it however long it takes. Writing this stub without the token made the "hangs" test below run for
/// the full thirty seconds and fail, which is exactly the lesson: the timeout is a contract between
/// the pipeline and the provider, and both halves have to hold up their end.
/// </remarks>
public sealed class StubNotificationProvider(Func<CancellationToken, Task> behaviour) : INotificationProvider
{
    public int Calls { get; private set; }

    public async Task SendAsync(NotificationKind kind, Guid orderId, string message, CancellationToken cancellationToken = default)
    {
        Calls++;

        await behaviour(cancellationToken);
    }
}

public class NotificationBusinessTests
{
    private readonly NotificationStore _store = new(Log.For<NotificationStore>());

    private NotificationBusinessManager Create(INotificationProvider provider, NotificationOptions? options = null)
    {
        var settings = options ?? new NotificationOptions { MaxRetryAttempts = 1, SendTimeout = TimeSpan.FromMilliseconds(100) };

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<NotificationProviderException>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = settings.MaxRetryAttempts,
                Delay = TimeSpan.Zero
            })
            .AddTimeout(settings.SendTimeout)
            .Build();

        return new NotificationBusinessManager(
            new TestPipelineProvider(NotificationBusinessManager.PipelineKey, pipeline),
            provider,
            _store,
            Log.For<NotificationBusinessManager>());
    }

    [Fact]
    public async Task A_successful_notification_is_recorded_as_Sent()
    {
        var business = Create(new StubNotificationProvider(_ => Task.CompletedTask));

        var record = await business.NotifyAsync(NotificationKind.OrderConfirmed, Guid.NewGuid(), "Your order is confirmed.");

        Assert.Equal(NotificationStatus.Sent, record.Status);
    }

    [Fact]
    public async Task A_provider_that_is_DOWN_drops_the_notification_and_NEVER_throws()
    {
        var provider = new StubNotificationProvider(_ => throw new NotificationProviderException("Provider is down."));

        var business = Create(provider);

        var record = await business.NotifyAsync(NotificationKind.OrderConfirmed, Guid.NewGuid(), "Your order is confirmed.");

        // The whole point of the service. An exception here would abandon the message, redeliver it,
        // and eventually dead-letter it — putting a failed EMAIL in the same queue operators watch
        // for stranded stock and un-refunded money. The order is already finished; nothing this
        // service does can improve it, and the only thing it can do is bury the signals that matter.
        Assert.Equal(NotificationStatus.Dropped, record.Status);
        Assert.NotEmpty(record.FailureReason);
    }

    [Fact]
    public async Task A_provider_that_HANGS_is_timed_out_rather_than_holding_the_pipeline_open()
    {
        var provider = new StubNotificationProvider(token => Task.Delay(TimeSpan.FromSeconds(30), token));

        var business = Create(provider);

        var record = await business.NotifyAsync(NotificationKind.OrderFailed, Guid.NewGuid(), "Your order failed.");

        // [R]3. Without the timeout this test would hang for thirty seconds — which is exactly what a
        // real hung provider would do to the subscription, with every later notification queued
        // behind it. A slow dependency takes the service down without ever returning an error.
        Assert.Equal(NotificationStatus.Dropped, record.Status);
    }

    [Fact]
    public async Task An_UNEXPECTED_failure_is_also_dropped_because_a_bug_here_must_not_disturb_a_finished_order()
    {
        var provider = new StubNotificationProvider(_ => throw new InvalidOperationException("A bug."));

        var business = Create(provider);

        var record = await business.NotifyAsync(NotificationKind.OrderConfirmed, Guid.NewGuid(), "Your order is confirmed.");

        Assert.Equal(NotificationStatus.Dropped, record.Status);
    }

    [Fact]
    public async Task A_failed_notification_is_retried_a_bounded_number_of_times_then_given_up_on()
    {
        var provider = new StubNotificationProvider(_ => throw new NotificationProviderException("Provider is down."));

        var business = Create(provider, new NotificationOptions { MaxRetryAttempts = 2, SendTimeout = TimeSpan.FromSeconds(1) });

        var record = await business.NotifyAsync(NotificationKind.OrderConfirmed, Guid.NewGuid(), "Your order is confirmed.");

        // One attempt plus two retries. Bounded, then dropped — never an unbounded loop.
        Assert.Equal(3, provider.Calls);
        Assert.Equal(3, record.Attempts);
        Assert.Equal(NotificationStatus.Dropped, record.Status);
    }
}
