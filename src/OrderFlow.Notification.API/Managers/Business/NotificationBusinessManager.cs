using Microsoft.Extensions.Options;
using OrderFlow.Notification.API.Managers.Data;
using OrderFlow.Notification.API.Managers.Domain;
using OrderFlow.Notification.API.Managers.Extensions;
using OrderFlow.Notification.API.Managers.ServiceModels;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;

namespace OrderFlow.Notification.API.Managers.Business;

public interface INotificationBusinessManager
{
    /// <summary>
    /// Try to tell the customer. <b>Never throws.</b> The return value is the record of what
    /// happened, and the caller is not expected to do anything about it.
    /// </summary>
    Task<NotificationRecord> NotifyAsync(
        NotificationKind kind,
        Guid orderId,
        string message,
        CancellationToken cancellationToken = default);

    IReadOnlyList<NotificationServiceModel> ListRecent(int maxRecords);

    IReadOnlyList<NotificationServiceModel> ListForOrder(Guid orderId);
}

/// <summary>
/// Best-effort delivery, and the one place in OrderFlow where "the operation failed" is an acceptable
/// place to stop.
/// </summary>
/// <remarks>
/// <para>
/// <b>NotifyAsync does not throw. That is the entire design, not an oversight.</b> Everywhere else in
/// this system, a handler that swallows an exception is a bug — a lost message is a lost order. Here
/// it is the requirement ([R]2). By the time this code runs, the order is already finished: the stock
/// is settled, the money is settled, the saga has reached a terminal state and gone home. There is
/// nothing left to roll back and nothing left to retry. An exception escaping this method would
/// abandon the message, redeliver it, and eventually dead-letter it — dragging a failed EMAIL into
/// the operational surface reserved for stranded stock and un-refunded money, and burying the signals
/// that actually matter under noise about an SMTP server.
/// </para>
/// <para>
/// So: bounded retries, a hard per-attempt timeout ([R]3), and then the notification is <b>dropped</b>
/// and recorded as dropped. A customer who did not get an email can be emailed again by a human. An
/// order stuck at Paid because the notification service dead-lettered its own message cannot be fixed
/// by anyone.
/// </para>
/// <para>
/// The timeout is not decoration. A provider that hangs rather than fails would otherwise hold the
/// subscription's handler open indefinitely, and every subsequent notification would queue up behind
/// it — the slow dependency taking the service down without ever returning an error.
/// </para>
/// <para>
/// <b>But the timeout is a CONTRACT, not a guarantee, and the provider has to keep its half.</b> Polly
/// times out by cancelling the CancellationToken; it cannot abandon a Task that ignores one. A
/// provider that blocks without observing cancellation — a synchronous SMTP client, say, or an SDK
/// that takes no token — cannot be timed out by this pipeline at all, and the handler will sit and
/// wait for it exactly as if the timeout were not there. <see cref="SimulatedNotificationProvider"/>
/// threads the token through for that reason. Anything real that replaces it must do the same, or be
/// wrapped in something that can genuinely abandon it. The unit test for this caught the mistake by
/// running for the full thirty seconds.
/// </para>
/// </remarks>
public class NotificationBusinessManager(
    ResiliencePipelineProvider<string> pipelineProvider,
    INotificationProvider provider,
    INotificationStore store,
    ILogger<NotificationBusinessManager> logger) : INotificationBusinessManager
{
    public const string PipelineKey = "notification";

    public async Task<NotificationRecord> NotifyAsync(
        NotificationKind kind,
        Guid orderId,
        string message,
        CancellationToken cancellationToken = default)
    {
        var record = new NotificationRecord
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Kind = kind,
            Message = message,
            CreatedUtc = DateTime.UtcNow
        };

        var pipeline = pipelineProvider.GetPipeline(PipelineKey);
        var attempts = 0;

        try
        {
            await pipeline.ExecuteAsync(
                async (state, token) =>
                {
                    attempts++;

                    await provider.SendAsync(state.kind, state.orderId, state.message, token);
                },
                (kind, orderId, message),
                cancellationToken);

            record.Status = NotificationStatus.Sent;
        }
        catch (Exception ex) when (ex is NotificationProviderException or TimeoutRejectedException)
        {
            // Expected failure modes: the provider said no, or took too long. Dropped, and loudly
            // enough that it is visible — but NOT rethrown.
            record.Status = NotificationStatus.Dropped;
            record.FailureReason = ex.Message;

            logger.LogWarning(
                ex,
                "Dropping {Kind} notification for order {OrderId} after {Attempts} attempt(s). The order is unaffected.",
                kind, orderId, attempts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything unexpected is ALSO dropped. This is the deliberate, load-bearing catch-all: a
            // bug in this service must not be able to reach back and disturb a completed order. The
            // only exception allowed out is cancellation, which means the host is shutting down and
            // the message should go back to the broker rather than be silently swallowed.
            record.Status = NotificationStatus.Dropped;
            record.FailureReason = ex.Message;

            logger.LogError(
                ex,
                "Unexpected failure sending {Kind} notification for order {OrderId}. Dropping. The order is unaffected.",
                kind, orderId);
        }

        record.Attempts = attempts;

        store.Add(record);

        return record;
    }

    public IReadOnlyList<NotificationServiceModel> ListRecent(int maxRecords) =>
        store.ListRecent(maxRecords).ToServiceModels();

    public IReadOnlyList<NotificationServiceModel> ListForOrder(Guid orderId) =>
        store.ListForOrder(orderId).ToServiceModels();
}

public static class NotificationBusinessExtensions
{
    public static IHostApplicationBuilder AddNotificationBusiness(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<NotificationOptions>(
            builder.Configuration.GetSection(NotificationOptions.SectionName));

        builder.Services.AddResiliencePipeline(NotificationBusinessManager.PipelineKey, (pipeline, context) =>
        {
            var settings = context.ServiceProvider.GetRequiredService<IOptions<NotificationOptions>>().Value;

            // Retry OUTSIDE the timeout, so the timeout applies per ATTEMPT rather than to the whole
            // sequence. A single hung send is abandoned and retried; it does not consume the entire
            // budget and leave the remaining attempts with no time to run.
            pipeline.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<NotificationProviderException>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = settings.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });

            // [R]3. The provider does not get to decide how long we wait.
            pipeline.AddTimeout(settings.SendTimeout);
        });

        builder.Services.AddSingleton<INotificationProvider, SimulatedNotificationProvider>();
        builder.Services.AddScoped<INotificationBusinessManager, NotificationBusinessManager>();

        return builder;
    }
}
