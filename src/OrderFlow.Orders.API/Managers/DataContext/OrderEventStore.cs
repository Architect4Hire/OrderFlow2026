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
    /// <summary>Appends one event to an order's stream. The only write this store has.</summary>
    Task AppendAsync(Guid orderId, object domainEvent, CancellationToken cancellationToken = default);

    /// <summary>Reads one order's full history, oldest first, from a single partition.</summary>
    Task<IReadOnlyList<OrderEventEnvelope>> ReadStreamAsync(Guid orderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// What actually lands in Cosmos. Deliberately not the Order aggregate: this is a record of
/// something that happened, and it is immutable. The aggregate's current state is a projection.
/// </summary>
public sealed class OrderEventEnvelope
{
    /// <summary>Cosmos requires the document key to be named <c>id</c>.</summary>
    [JsonPropertyName("id")]
    public Guid EventId { get; set; }

    /// <summary>The partition key. Serializes to <c>orderId</c>, matching the AppHost's "/orderId".</summary>
    public Guid OrderId { get; set; }

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

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task AppendAsync(Guid orderId, object domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var envelope = new OrderEventEnvelope
        {
            EventId = Guid.NewGuid(),
            OrderId = orderId,
            Type = domainEvent.GetType().Name,
            OccurredUtc = DateTime.UtcNow,
            Payload = JsonSerializer.SerializeToElement(domainEvent, domainEvent.GetType(), PayloadOptions)
        };

        // CreateItemAsync, never UpsertItemAsync. Upsert would silently overwrite an existing
        // event and quietly turn the audit trail into a mutable record — the one thing an event
        // log must never be. A colliding id has to fail loudly instead.
        await container.CreateItemAsync(
            envelope,
            new PartitionKey(orderId.ToString()),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Appended {EventType} to order {OrderId} stream. EventId {EventId}",
            envelope.Type, orderId, envelope.EventId);
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

        // TODO: a per-stream monotonic sequence number would give a total order. OccurredUtc can
        // tie if two events are appended within the same clock tick, and Cosmos cannot break the
        // tie on a second field without a composite index.
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.occurredUtc");

        var events = new List<OrderEventEnvelope>();

        using var iterator = container.GetItemQueryIterator<OrderEventEnvelope>(query, requestOptions: options);

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            events.AddRange(page);
        }

        return events;
    }
}

/// <summary>Registration for the event store. Called from Program.cs (Prompt B11).</summary>
public static class OrderEventStoreExtensions
{
    public static IHostApplicationBuilder AddOrderEventStore(this IHostApplicationBuilder builder)
    {
        builder.AddAzureCosmosContainer(
            OrderEventStore.ContainerResourceName,
            configureClientOptions: options =>
            {
                // Load-bearing. The container's partition key path is "/orderId", but the Cosmos
                // SDK's default serializer writes properties as declared — "OrderId" — and every
                // single write would fail on a partition-key mismatch. The Web defaults give us
                // camelCase, which is what the AppHost declared.
                options.UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            });

        builder.Services.AddScoped<IOrderEventStore, OrderEventStore>();

        return builder;
    }
}
