using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Orders.API.Managers.DataContext;

/// <summary>One handled message. The document's existence IS the record.</summary>
public sealed class ProcessedMessageDocument
{
    /// <summary>The MessageId. Half of the key, and the Cosmos document id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The consumer. The other half of the key, and the partition key.</summary>
    public string ConsumerName { get; set; } = string.Empty;

    public DateTime ProcessedUtc { get; set; }
}

/// <summary>
/// The saga's durable idempotency store, in Cosmos.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the in-memory store for the Order service, and the difference is not academic: the
/// in-memory one forgets every processed message the instant the process restarts, so a redelivered
/// event after a deploy would be handled a second time. The whole architecture claims at-least-once
/// delivery is safe here; that claim was previously being carried by the saga's terminal guard alone.
/// </para>
/// <para>
/// <c>(ConsumerName, MessageId)</c> maps exactly onto <c>(partition key, id)</c>, which makes the
/// read a single point lookup rather than a query, and — more importantly — makes the WRITE the
/// check: <c>CreateItemAsync</c> either creates the document or returns 409. Two concurrent
/// redeliveries cannot both conclude "not processed yet" and proceed, because only one of them can
/// create the document.
/// </para>
/// </remarks>
public sealed class CosmosIdempotencyKeyStore(
    Container container,
    ILogger<CosmosIdempotencyKeyStore> logger) : IIdempotencyKeyStore
{
    /// <summary>Matches the container resource the AppHost declares.</summary>
    public const string ContainerResourceName = "processed-messages";

    public async Task<bool> HasProcessedAsync(
        string consumerName,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        try
        {
            await container.ReadItemAsync<ProcessedMessageDocument>(
                messageId.ToString(),
                new PartitionKey(consumerName),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task MarkProcessedAsync(
        string consumerName,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        var document = new ProcessedMessageDocument
        {
            Id = messageId.ToString(),
            ConsumerName = consumerName,
            ProcessedUtc = DateTime.UtcNow
        };

        try
        {
            await container.CreateItemAsync(
                document,
                new PartitionKey(consumerName),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Already marked — by a concurrent redelivery of the same message. Marking twice is
            // exactly as good as marking once, so this is not an error.
            logger.LogDebug(
                "{ConsumerName} had already marked message {MessageId} as processed.", consumerName, messageId);
        }
    }
}

public static class CosmosIdempotencyKeyStoreExtensions
{
    /// <summary>
    /// Registers the store against its OWN Cosmos container, resolved by key. Both of the Order
    /// service's containers are keyed: an unkeyed <c>Container</c> registration would let the event
    /// stream and the idempotency store silently resolve to whichever was registered last, and the
    /// symptom would be processed-message documents landing in the event log.
    /// </summary>
    public static IHostApplicationBuilder AddCosmosIdempotencyKeyStore(this IHostApplicationBuilder builder)
    {
        builder.AddKeyedAzureCosmosContainer(
            CosmosIdempotencyKeyStore.ContainerResourceName,
            configureClientOptions: options =>
            {
                // Same camelCase requirement as the event store: the partition key path is
                // "/consumerName", and the SDK's default serializer would write "ConsumerName".
                options.UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            });

        // Registered HERE, and therefore BEFORE AddOrderFlowMessaging(), which only TryAdds the
        // in-memory fallback. Call these two the other way round and the service silently keeps the
        // volatile store — the failure mode being a redelivery after a restart that nobody catches.
        builder.Services.AddScoped<IIdempotencyKeyStore>(provider => new CosmosIdempotencyKeyStore(
            provider.GetRequiredKeyedService<Container>(CosmosIdempotencyKeyStore.ContainerResourceName),
            provider.GetRequiredService<ILogger<CosmosIdempotencyKeyStore>>()));

        return builder;
    }
}
