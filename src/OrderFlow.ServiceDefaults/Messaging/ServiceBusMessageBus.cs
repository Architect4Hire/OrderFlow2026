using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts.Messages;

namespace OrderFlow.ServiceDefaults.Messaging;

/// <summary>
/// <see cref="IMessageBus"/> over the Azure Service Bus SDK. Runs unchanged against the
/// emulator the AppHost starts — the only difference is the connection string Aspire injects.
/// </summary>
public sealed class ServiceBusMessageBus(
    ServiceBusClient client,
    ILogger<ServiceBusMessageBus> logger) : IMessageBus, IAsyncDisposable
{
    /// <summary>W3C trace context keys, written to <c>ApplicationProperties</c> on every outgoing message.</summary>
    public const string TraceParentPropertyKey = "traceparent";
    public const string TraceStatePropertyKey = "tracestate";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>camelCase of <see cref="MessageBase.MessageId"/> under <see cref="JsonSerializerDefaults.Web"/>.</summary>
    private const string MessageIdJsonProperty = "messageId";
    private const string OccurredUtcJsonProperty = "occurredUtc";

    // Senders are thread-safe and hold an AMQP link each, so they are created once and reused.
    // Lazy<T> because ConcurrentDictionary.GetOrAdd may run its factory more than once under
    // contention, which would orphan an undisposed sender.
    private readonly ConcurrentDictionary<string, Lazy<ServiceBusSender>> _senders = new();

    public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : MessageBase
        => DispatchAsync(command, "command", cancellationToken);

    public Task PublishEventAsync<T>(T @event, CancellationToken cancellationToken = default) where T : MessageBase
        => DispatchAsync(@event, "event", cancellationToken);

    private async Task DispatchAsync<T>(T message, string kind, CancellationToken cancellationToken) where T : MessageBase
    {
        ArgumentNullException.ThrowIfNull(message);

        // The runtime type, not typeof(T): a caller holding the message as MessageBase would
        // otherwise route to the wrong entity and serialize only the base properties.
        var messageType = message.GetType();
        var entityName = MessagingConventions.EntityNameFor(messageType);

        // Correlation flows from the originating OrderPlaced and never changes for that order.
        // An unset CorrelationId is a bug in the caller — throw rather than mint one, which
        // would silently split an order's history into two untraceable halves.
        if (message.CorrelationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"{messageType.Name} has no CorrelationId. Correlation is carried from the originating " +
                "OrderPlaced and must be set by the caller; the bus will not generate one.");
        }

        // MessageId is the only value the bus mints, and only when the caller left it unset.
        // It is the key the consumer-side idempotency guard dedupes on: (ConsumerName, MessageId).
        // A redelivery of this message carries the same MessageId, which is what makes the
        // second delivery a no-op.
        var messageId = message.MessageId == Guid.Empty ? Guid.NewGuid() : message.MessageId;
        var occurredUtc = message.OccurredUtc == default ? DateTime.UtcNow : message.OccurredUtc;

        var body = JsonSerializer.SerializeToNode(message, messageType, SerializerOptions)!.AsObject();
        body[MessageIdJsonProperty] = JsonValue.Create(messageId);
        body[OccurredUtcJsonProperty] = JsonValue.Create(occurredUtc);

        var serviceBusMessage = new ServiceBusMessage(BinaryData.FromString(body.ToJsonString(SerializerOptions)))
        {
            // Mirrored onto the envelope so a consumer can dedupe and correlate without
            // deserializing the body, and so the broker's own duplicate detection can key on it.
            MessageId = messageId.ToString(),
            CorrelationId = message.CorrelationId.ToString(),
            Subject = messageType.Name,
            ContentType = "application/json"
        };

        InjectTraceContext(serviceBusMessage);

        var sender = _senders.GetOrAdd(entityName, name => new Lazy<ServiceBusSender>(() => client.CreateSender(name))).Value;

        try
        {
            await sender.SendMessageAsync(serviceBusMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log for the operator, then rethrow. A publish that fails silently is the worst
            // failure mode in this system: the saga would sit waiting for a reply to a message
            // that was never sent, and the order would hang forever with no compensation.
            logger.LogError(
                ex,
                "Failed to dispatch {Kind} {MessageType} to {EntityName}. MessageId {MessageId}, CorrelationId {CorrelationId}",
                kind, messageType.Name, entityName, messageId, message.CorrelationId);
            throw;
        }

        logger.LogInformation(
            "Dispatched {Kind} {MessageType} to {EntityName}. MessageId {MessageId}, CorrelationId {CorrelationId}",
            kind, messageType.Name, entityName, messageId, message.CorrelationId);
    }

    /// <summary>
    /// Writes the ambient trace context onto the message as W3C <c>traceparent</c>/<c>tracestate</c>.
    /// </summary>
    /// <remarks>
    /// The Azure SDK also stamps its own legacy <c>Diagnostic-Id</c> property during send, but the
    /// standard key is what the consumer restores its parent span from — this is the link that keeps
    /// every hop of one order inside a single distributed trace instead of nine disconnected ones.
    /// </remarks>
    private static void InjectTraceContext(ServiceBusMessage message)
    {
        var activity = Activity.Current;

        if (activity is null)
        {
            return;
        }

        DistributedContextPropagator.Current.Inject(activity, message.ApplicationProperties, static (carrier, key, value) =>
        {
            if (carrier is IDictionary<string, object> properties && value is not null)
            {
                properties[key] = value;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            if (sender.IsValueCreated)
            {
                await sender.Value.DisposeAsync().ConfigureAwait(false);
            }
        }

        _senders.Clear();
    }
}
