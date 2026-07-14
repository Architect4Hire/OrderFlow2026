using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts.Messages;

namespace OrderFlow.ServiceDefaults.Messaging;

/// <summary>
/// The one place a message is settled. Every consumer in OrderFlow — commands off a queue, events
/// off a topic subscription — inherits the same three guarantees from here, because a service that
/// gets any of them wrong loses orders quietly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is completed until the handler has succeeded.</b> <c>AutoCompleteMessages</c> is off,
/// so a handler that throws leaves the message unsettled: it is abandoned, redelivered, and after
/// MaxDeliveryCount attempts it dead-letters where a human can see it. Completing first — the SDK
/// default, and the easy mistake — turns every transient fault into a silently dropped order. This
/// is also what makes "publish the reply INSIDE the handler" sufficient: if the publish throws, the
/// command is never settled and the whole thing is retried, so the sender is never left waiting on
/// a reply that no longer exists.
/// </para>
/// <para>
/// <b>The idempotency key is (ConsumerName, MessageId), never MessageId alone.</b> One event can
/// fan out to several subscribers; key on the message alone and whichever consumer ran first would
/// suppress the others.
/// </para>
/// <para>
/// <b>Poison messages dead-letter immediately.</b> An unparseable id or body cannot be fixed by
/// retrying it, so it does not get to burn the delivery count first.
/// </para>
/// </remarks>
public abstract class ServiceBusConsumer<TMessage>(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
    where TMessage : MessageBase
{
    /// <summary>
    /// The running service. ServiceDefaults passes this same name to <c>AddSource(...)</c>, and it
    /// is also what qualifies <see cref="ConsumerName"/>.
    /// </summary>
    private static readonly string ApplicationName = Assembly.GetEntryAssembly()?.GetName().Name ?? "OrderFlow";

    /// <summary>
    /// Named for the entry assembly, which is the ApplicationName that ServiceDefaults passes to
    /// <c>AddSource(...)</c>. Get this wrong and the spans are simply dropped, and the end-to-end
    /// trace — the thing that proves the saga works — quietly loses every consumer hop.
    /// </summary>
    private static readonly ActivitySource ActivitySource = new(ApplicationName);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private ServiceBusProcessor? _processor;

    /// <summary>
    /// <c>null</c> for a command queue (exactly one handler). A topic subscription name for an
    /// event (many subscribers).
    /// </summary>
    protected virtual string? SubscriptionName => null;

    /// <summary>
    /// How many messages this consumer handles at once. One by default, which is the safe answer.
    /// <b>Raise it where concurrency is the behaviour under test</b> — a consumer pinned at 1 cannot
    /// contend with itself, so a race the design is supposed to survive will simply never occur.
    /// </summary>
    protected virtual int MaxConcurrentCalls => 1;

    /// <summary>
    /// The ConsumerName half of the idempotency key, qualified by the SERVICE.
    /// </summary>
    /// <remarks>
    /// The class name alone is not unique across the system: the order saga and the notification
    /// service both have a <c>PaymentDeclinedConsumer</c>, and <c>payment-declined</c> is precisely
    /// the topic with two subscribers. Keyed on the bare class name, a durable shared store would let
    /// whichever service handled the event first suppress the other — the saga would compensate and
    /// the customer would never be told, or the reverse. Harmless only while every store is
    /// process-local; a latent, silent bug the moment one is not.
    /// </remarks>
    protected string ConsumerName => $"{ApplicationName}.{GetType().Name}";

    /// <summary>
    /// Handle the message. Runs inside a fresh DI scope, after the idempotency guard has already
    /// cleared it. Throw to retry; return to settle.
    /// </summary>
    protected abstract Task HandleAsync(IServiceProvider services, TMessage message, CancellationToken cancellationToken);

    /// <summary>How long to keep trying to attach to the broker before giving up and taking the host down.</summary>
    /// <remarks>
    /// Generous on purpose. The cost of waiting too long is a slow start; the cost of giving up too early
    /// is a dead service. Only the second one wakes anybody up.
    /// </remarks>
    private static readonly TimeSpan StartupConnectBudget = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan MaximumConnectBackoff = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same convention the publisher used, so a renamed contract breaks both ends together
        // instead of silently routing into the void.
        var entityName = MessagingConventions.EntityNameFor<TMessage>();

        var options = new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxConcurrentCalls = MaxConcurrentCalls
        };

        _processor = SubscriptionName is null
            ? client.CreateProcessor(entityName, options)
            : client.CreateProcessor(entityName, SubscriptionName, options);

        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnProcessorErrorAsync;

        await StartProcessingWithRetryAsync(entityName, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches to the broker, retrying while it is merely NOT READY YET, and rethrowing once it is
    /// clear it never will be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A bare <c>StartProcessingAsync</c> here is a coin flip that takes the whole service with it.</b>
    /// An unhandled exception in a <see cref="BackgroundService"/> stops the host
    /// (<c>BackgroundServiceExceptionBehavior.StopHost</c>, the .NET default and the right one), so a
    /// single consumer that cannot attach kills the entire API. Aspire's <c>WaitFor</c> does not close
    /// this window: it waits on the broker's CONTAINER health check, and the Service Bus emulator accepts
    /// TCP before it has finished materialising queues, topics and subscriptions from its config. Connect
    /// in that gap and you get a communication error or MessagingEntityNotFound.
    /// </para>
    /// <para>
    /// The odds scale with consumer count, which is why this surfaced as "the Orders API randomly does
    /// not start": Orders attaches SIX processors, Fulfillment one. Six rolls of the dice against one.
    /// It failed roughly one start in six, and — because the host simply exited — the AppHost logged
    /// nothing at all. The other four services came up healthy, so the only symptom was a dead front page.
    /// </para>
    /// <para>
    /// <b>Retrying is the fix; swallowing is NOT.</b> Catching this and carrying on would leave the host
    /// alive with a consumer that never attached — messages piling up in a queue nobody is reading, no
    /// error anywhere, an order that simply stops moving. That is strictly worse than crashing, and it is
    /// the exact silent-failure class this system exists to eliminate. So: retry while it looks like a
    /// race, and if the budget runs out, <b>rethrow and let the host die loudly</b> — because by then it
    /// is not a race, it is a misconfiguration (an entity the AppHost never declared), and a service that
    /// cannot consume its own queue has no business reporting Healthy.
    /// </para>
    /// </remarks>
    private async Task StartProcessingWithRetryAsync(string entityName, CancellationToken stoppingToken)
    {
        var target = SubscriptionName is null ? entityName : $"{entityName}/{SubscriptionName}";
        var deadline = DateTime.UtcNow + StartupConnectBudget;
        var backoff = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _processor!.StartProcessingAsync(stoppingToken).ConfigureAwait(false);

                // Logged only once it is TRUE. Announcing "listening" before the attach succeeded is how
                // a dead consumer ends up with a reassuring log line above it.
                logger.LogInformation(
                    "{Consumer} listening on {Target} with concurrency {Concurrency} (attempt {Attempt})",
                    ConsumerName, target, MaxConcurrentCalls, attempt);

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down mid-attach is not a failure.
                throw;
            }
            catch (Exception ex) when (DateTime.UtcNow < deadline)
            {
                logger.LogWarning(
                    ex,
                    "{Consumer} could not attach to {Target} (attempt {Attempt}). The broker may still be starting. Retrying in {Backoff}.",
                    ConsumerName, target, attempt, backoff);

                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumConnectBackoff.Ticks));
            }
            catch (Exception ex)
            {
                // Budget exhausted. This is no longer a startup race — the entity is missing, the
                // connection string is wrong, or the broker is genuinely down. Take the host with us:
                // a service that cannot read its own queue must not sit there looking healthy.
                logger.LogCritical(
                    ex,
                    "{Consumer} gave up attaching to {Target} after {Budget}. Stopping the host — this service cannot do its job.",
                    ConsumerName, target, StartupConnectBudget);

                throw;
            }
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        var cancellationToken = args.CancellationToken;

        // The bus mirrors MessageId onto the envelope precisely so we can dedupe without first
        // deserializing the body. A message without a usable one can never be deduped, so it is
        // poison.
        if (!Guid.TryParse(message.MessageId, out var messageId))
        {
            await args.DeadLetterMessageAsync(
                message, "InvalidMessageId", $"MessageId '{message.MessageId}' is not a Guid.", cancellationToken).ConfigureAwait(false);

            return;
        }

        // Re-parent onto the producer's trace so this hop joins the order's single end-to-end trace
        // rather than starting an orphan.
        var parentId = message.ApplicationProperties.TryGetValue(ServiceBusMessageBus.TraceParentPropertyKey, out var raw)
            ? raw as string
            : null;

        using var activity = ActivitySource.StartActivity($"{ConsumerName} process", ActivityKind.Consumer, parentId);

        TMessage? payload;

        try
        {
            payload = JsonSerializer.Deserialize<TMessage>(message.Body.ToString(), SerializerOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "{Consumer} could not deserialize message {MessageId}", ConsumerName, messageId);

            await args.DeadLetterMessageAsync(
                message, "DeserializationFailed", ex.Message, cancellationToken).ConfigureAwait(false);

            return;
        }

        if (payload is null)
        {
            await args.DeadLetterMessageAsync(
                message, "EmptyBody", $"{typeof(TMessage).Name} body deserialized to null.", cancellationToken).ConfigureAwait(false);

            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();

        var idempotencyStore = scope.ServiceProvider.GetRequiredService<IIdempotencyKeyStore>();

        try
        {
            if (await idempotencyStore.HasProcessedAsync(ConsumerName, messageId, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "{Consumer} skipping already-processed message {MessageId} (order {OrderId})",
                    ConsumerName, messageId, payload.CorrelationId);

                await args.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);

                return;
            }

            await HandleAsync(scope.ServiceProvider, payload, cancellationToken).ConfigureAwait(false);

            await idempotencyStore.MarkProcessedAsync(ConsumerName, messageId, cancellationToken).ConfigureAwait(false);

            // Only now.
            await args.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Abandon rather than complete: the message goes back, the delivery count rises, and
            // after MaxDeliveryCount it dead-letters. Losing it here would lose the order.
            logger.LogError(
                ex,
                "{Consumer} failed on message {MessageId} (order {OrderId}), delivery {DeliveryCount}. Abandoning for retry.",
                ConsumerName, messageId, payload.CorrelationId, message.DeliveryCount);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            await args.AbandonMessageAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private Task OnProcessorErrorAsync(ProcessErrorEventArgs args)
    {
        // Transport-level faults (lock lost, connection dropped). The SDK keeps going; we just want
        // them visible rather than swallowed.
        logger.LogError(
            args.Exception,
            "{Consumer} processor error during {Operation} on {Entity}",
            ConsumerName, args.ErrorSource, args.EntityPath);

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
            await _processor.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
