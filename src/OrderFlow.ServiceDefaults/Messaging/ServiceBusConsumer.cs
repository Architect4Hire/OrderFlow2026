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
    /// Named for the entry assembly, which is the ApplicationName that ServiceDefaults passes to
    /// <c>AddSource(...)</c>. Get this wrong and the spans are simply dropped, and the end-to-end
    /// trace — the thing that proves the saga works — quietly loses every consumer hop.
    /// </summary>
    private static readonly ActivitySource ActivitySource =
        new(Assembly.GetEntryAssembly()?.GetName().Name ?? "OrderFlow");

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

    /// <summary>Distinct per consumer — this is the ConsumerName half of the idempotency key.</summary>
    protected string ConsumerName => GetType().Name;

    /// <summary>
    /// Handle the message. Runs inside a fresh DI scope, after the idempotency guard has already
    /// cleared it. Throw to retry; return to settle.
    /// </summary>
    protected abstract Task HandleAsync(IServiceProvider services, TMessage message, CancellationToken cancellationToken);

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

        logger.LogInformation(
            "{Consumer} listening on {Entity}{Subscription} with concurrency {Concurrency}",
            ConsumerName, entityName, SubscriptionName is null ? string.Empty : $"/{SubscriptionName}", MaxConcurrentCalls);

        await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
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
