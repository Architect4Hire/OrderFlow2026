using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.Saga;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Orders.API.Managers.Consumers;

/// <summary>
/// How the saga hears back from the reacting services. One subclass per inbound event, each doing
/// exactly three things: guard idempotency, call the saga, mark processed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is completed until the saga has succeeded.</b> <c>AutoCompleteMessages</c> is off, so
/// a handler that throws leaves the message unsettled: it is abandoned, redelivered, and after
/// MaxDeliveryCount attempts it dead-letters where a human can see it. Completing first — the
/// default, and the easy mistake — would turn every transient fault into a silently dropped order.
/// </para>
/// <para>
/// <b>The idempotency key is (ConsumerName, MessageId), never MessageId alone.</b> A single event
/// fans out to several subscribers: PaymentDeclined is handled by both this saga and the
/// notification service. Key on the message alone and whichever consumer ran first would suppress
/// the other.
/// </para>
/// </remarks>
public abstract class OrderSagaConsumer<TEvent>(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger logger) : BackgroundService
    where TEvent : MessageBase
{
    /// <summary>The subscription the AppHost declares on each of the saga's topics.</summary>
    private const string SubscriptionName = "order-saga";

    /// <summary>
    /// Matches the ActivitySource ServiceDefaults registers (the application name), so these spans
    /// land in the same trace as the HTTP and Service Bus ones rather than being dropped.
    /// </summary>
    private static readonly ActivitySource ActivitySource = new("OrderFlow.Orders.API");

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private ServiceBusProcessor? _processor;

    /// <summary>Distinct per consumer — this is the ConsumerName half of the idempotency key.</summary>
    private string ConsumerName => GetType().Name;

    /// <summary>The one line a subclass writes: hand the event to the matching saga method.</summary>
    protected abstract Task HandleAsync(IOrderSaga saga, TEvent message, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Topic name comes from the same convention the publisher used, so a renamed contract
        // breaks both ends together instead of silently routing into the void.
        var topicName = MessagingConventions.EntityNameFor<TEvent>();

        _processor = client.CreateProcessor(topicName, SubscriptionName, new ServiceBusProcessorOptions
        {
            // [R]2. The SDK will not settle anything on our behalf.
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxConcurrentCalls = 1
        });

        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnProcessorErrorAsync;

        logger.LogInformation("{Consumer} listening on {Topic}/{Subscription}", ConsumerName, topicName, SubscriptionName);

        await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        var cancellationToken = args.CancellationToken;

        // The bus mirrors MessageId onto the envelope precisely so we can dedupe without first
        // deserializing the body. A message without a usable one can never be deduped, so it is
        // poison — dead-letter it rather than retry it forever.
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

        TEvent? @event;

        try
        {
            @event = JsonSerializer.Deserialize<TEvent>(message.Body.ToString(), SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Malformed body: retrying cannot fix it. Dead-letter immediately instead of burning
            // the delivery count.
            logger.LogError(ex, "{Consumer} could not deserialize message {MessageId}", ConsumerName, messageId);

            await args.DeadLetterMessageAsync(
                message, "DeserializationFailed", ex.Message, cancellationToken).ConfigureAwait(false);

            return;
        }

        if (@event is null)
        {
            await args.DeadLetterMessageAsync(
                message, "EmptyBody", $"{typeof(TEvent).Name} body deserialized to null.", cancellationToken).ConfigureAwait(false);

            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();

        var idempotencyStore = scope.ServiceProvider.GetRequiredService<IIdempotencyKeyStore>();
        var saga = scope.ServiceProvider.GetRequiredService<IOrderSaga>();

        try
        {
            if (await idempotencyStore.HasProcessedAsync(ConsumerName, messageId, cancellationToken).ConfigureAwait(false))
            {
                // The duplicate-payment-callback case. Settle it and do nothing else.
                logger.LogInformation(
                    "{Consumer} skipping already-processed message {MessageId} (order {OrderId})",
                    ConsumerName, messageId, @event.CorrelationId);

                await args.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);

                return;
            }

            // [R]1: the consumer's entire contribution. Everything it knows about this event is
            // which saga method to call.
            await HandleAsync(saga, @event, cancellationToken).ConfigureAwait(false);

            await idempotencyStore.MarkProcessedAsync(ConsumerName, messageId, cancellationToken).ConfigureAwait(false);

            // Only now.
            await args.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Abandon rather than complete: the message goes back, the delivery count rises, and
            // after MaxDeliveryCount it dead-letters. Losing it here would lose the order.
            //
            // If the saga succeeded but MarkProcessed or Complete then failed, the redelivery will
            // run the saga again — which is safe by design: its terminal guard and deterministic
            // MessageIds make a replay a no-op rather than a second refund (B8).
            logger.LogError(
                ex,
                "{Consumer} failed on message {MessageId} (order {OrderId}), delivery {DeliveryCount}. Abandoning for retry.",
                ConsumerName, messageId, @event.CorrelationId, message.DeliveryCount);

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
