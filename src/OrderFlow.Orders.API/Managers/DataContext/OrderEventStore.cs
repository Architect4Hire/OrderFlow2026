using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace OrderFlow.Orders.API.Managers.DataContext;

/// <summary>
/// The append-only log of everything that has happened to an order. The system of record: the
/// Redis read model (Prompt B6) is a projection of this and can be rebuilt from it, never the
/// other way round.
/// </summary>
public interface IOrderEventStore
{
    /// <summary>
    /// Appends one event to an order's stream. The only write this store has, and it is idempotent:
    /// appending an event type the stream already carries is a no-op, not a duplicate.
    /// </summary>
    Task AppendAsync(Guid orderId, object domainEvent, CancellationToken cancellationToken = default);

    /// <summary>Reads one order's full history, oldest first, from a single partition.</summary>
    Task<IReadOnlyList<OrderEventEnvelope>> ReadStreamAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every order id that has a stream. A CROSS-PARTITION scan, and the only one in the system —
    /// it exists solely so the Redis projection can be rebuilt from scratch (ADR-003). Never call
    /// it on a request path.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListOrderIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What actually lands in Cosmos. Deliberately not the Order aggregate: this is a record of
/// something that happened, and it is immutable. The aggregate's current state is a projection.
/// </summary>
public sealed class OrderEventEnvelope
{
    /// <summary>
    /// Cosmos requires the document key to be named <c>id</c>. It is
    /// <c>{orderId:N}-{sequence:D4}</c>, which is what makes the sequence <b>enforced</b> rather
    /// than merely recorded: two writers who both think they are event 3 collide on the key, and
    /// one of them is told so.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The partition key. Serializes to <c>orderId</c>, matching the AppHost's "/orderId".</summary>
    public Guid OrderId { get; set; }

    /// <summary>
    /// Position in this order's stream, from 1. <b>This, not OccurredUtc, is the order of history.</b>
    /// A timestamp cannot be trusted to sort a stream: two events written in the same clock tick tie,
    /// and the tie is broken arbitrarily — so an order could rehydrate as Paid-then-Reserved and the
    /// saga would draw a conclusion from a history that never happened.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>The event's type name — e.g. <c>PaymentDeclined</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public DateTime OccurredUtc { get; set; }

    /// <summary>The event as it happened, stored whole so the audit trail keeps its detail.</summary>
    public JsonElement Payload { get; set; }
}

/// <inheritdoc cref="IOrderEventStore"/>
public sealed class OrderEventStore(Container container, ILogger<OrderEventStore> logger) : IOrderEventStore
{
    /// <summary>Matches the container resource the AppHost declares.</summary>
    public const string ContainerResourceName = "order-events";

    /// <summary>Bounded, like every other retry in this system. Three lost races means real contention.</summary>
    private const int MaxAppendAttempts = 3;

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task AppendAsync(Guid orderId, object domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var eventType = domainEvent.GetType().Name;

        for (var attempt = 1; attempt <= MaxAppendAttempts; attempt++)
        {
            var stream = await ReadStreamAsync(orderId, cancellationToken).ConfigureAwait(false);

            // Idempotent append. A redelivered event re-enters the saga, the saga re-applies it, and
            // without this the stream grows a second identical entry every time the broker retries.
            // The saga's design guarantees each event type occurs at most once per order, so "this
            // type is already here" is exactly "we have already seen this event".
            //
            // NOTE: that guarantee is the saga's, not the store's. An event type that could
            // legitimately repeat for one order would need a different key (the source MessageId).
            if (stream.Any(existing => existing.Type == eventType))
            {
                logger.LogInformation(
                    "{EventType} is already in order {OrderId}'s stream. Ignoring the duplicate append.",
                    eventType, orderId);

                return;
            }

            var sequence = stream.Count == 0 ? 1 : stream[^1].Sequence + 1;

            var envelope = new OrderEventEnvelope
            {
                Id = EventId(orderId, sequence),
                OrderId = orderId,
                Sequence = sequence,
                Type = eventType,
                OccurredUtc = DateTime.UtcNow,
                Payload = JsonSerializer.SerializeToElement(domainEvent, domainEvent.GetType(), PayloadOptions)
            };

            try
            {
                // CreateItemAsync, never UpsertItemAsync. Upsert would silently overwrite an existing
                // event and quietly turn the audit trail into a mutable record — the one thing an
                // event log must never be. A colliding id has to fail loudly instead, and here we
                // catch that failure and use it.
                await container.CreateItemAsync(
                    envelope,
                    new PartitionKey(orderId.ToString()),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    "Appended {EventType} to order {OrderId} at sequence {Sequence}.",
                    eventType, orderId, sequence);

                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Someone else claimed this sequence between our read and our write. Nothing was
                // written. Re-read and take the next slot — the append-time equivalent of Inventory's
                // row-version retry, and the reason two saga handlers racing on one order cannot
                // interleave their history.
                logger.LogWarning(
                    "Sequence {Sequence} for order {OrderId} was taken by a concurrent append (attempt {Attempt} of {MaxAttempts}). Retrying.",
                    sequence, orderId, attempt, MaxAppendAttempts);
            }
        }

        throw new InvalidOperationException(
            $"Could not append {eventType} to order {orderId} after {MaxAppendAttempts} sequence conflicts.");
    }

    public async Task<IReadOnlyList<OrderEventEnvelope>> ReadStreamAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // Pinning the PartitionKey is what keeps this a single-partition read. Without it Cosmos
        // would fan the query out across every partition — the cross-partition scan [R]2 forbids,
        // and the reason status reads come from Redis rather than from here.
        var options = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(orderId.ToString())
        };

        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.sequence");

        var events = new List<OrderEventEnvelope>();

        using var iterator = container.GetItemQueryIterator<OrderEventEnvelope>(query, requestOptions: options);

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            events.AddRange(page);
        }

        return events;
    }

    public async Task<IReadOnlyList<Guid>> ListOrderIdsAsync(CancellationToken cancellationToken = default)
    {
        // The one deliberate cross-partition query in OrderFlow. It is a rebuild operation, not a
        // request path, and it is confined to this method so that "we never fan out across
        // partitions" stays a true statement about everything else.
        var query = new QueryDefinition("SELECT DISTINCT VALUE c.orderId FROM c");

        var orderIds = new List<Guid>();

        using var iterator = container.GetItemQueryIterator<Guid>(query);

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            orderIds.AddRange(page);
        }

        return orderIds;
    }

    private static string EventId(Guid orderId, long sequence) => $"{orderId:N}-{sequence:D4}";
}

/// <summary>Registration for the event store. Called from Program.cs (Prompt B11).</summary>
public static class OrderEventStoreExtensions
{
    public static IHostApplicationBuilder AddOrderEventStore(this IHostApplicationBuilder builder)
    {
        // KEYED, because Orders now holds two containers — the event stream and the durable
        // idempotency store — and an unkeyed Container registration would let one silently win.
        builder.AddKeyedAzureCosmosContainer(
            OrderEventStore.ContainerResourceName,
            configureClientOptions: options =>
            {
                // Load-bearing. The container's partition key path is "/orderId", but the Cosmos
                // SDK's default serializer writes properties as declared — "OrderId" — and every
                // single write would fail on a partition-key mismatch. The Web defaults give us
                // camelCase, which is what the AppHost declared.
                options.UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            });

        builder.Services.AddScoped<IOrderEventStore>(provider => new OrderEventStore(
            provider.GetRequiredKeyedService<Container>(OrderEventStore.ContainerResourceName),
            provider.GetRequiredService<ILogger<OrderEventStore>>()));

        return builder;
    }
}
