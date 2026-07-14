using System.Collections.Concurrent;

namespace OrderFlow.ServiceDefaults.Messaging;

/// <summary>
/// Records the <c>(ConsumerName, MessageId)</c> pairs a service has already handled, so a
/// redelivered message is a no-op instead of a second charge, a second reservation, or a
/// second shipment.
/// </summary>
/// <remarks>
/// <para>
/// Service Bus delivers at-least-once. Redelivery is not an edge case here — it is the normal
/// consequence of a consumer crashing after doing its work but before settling the message, or
/// of the lock simply expiring under load. Every consumer runs this guard; none of them may opt out.
/// </para>
/// <para>
/// The pair is keyed on the consumer, not just the message, because an event fans out to several
/// subscribers: <c>PaymentDeclined</c> is handled by both the order saga and the notification
/// service, and each must process its own copy exactly once.
/// </para>
/// <para>
/// Call order is <see cref="HasProcessedAsync"/> → handle → <see cref="MarkProcessedAsync"/>.
/// Marking first would lose the message entirely if the handler then crashed. A durable
/// implementation should commit the mark in the same transaction as the handler's own state
/// change, which closes the window where a crash between handling and marking causes a replay.
/// </para>
/// </remarks>
public interface IIdempotencyKeyStore
{
    /// <summary>True if this consumer has already handled this message and should skip it.</summary>
    Task<bool> HasProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Records that this consumer has handled this message. Called after the handler succeeds.</summary>
    Task MarkProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken = default);
}

// TODO: shared durable store. This in-memory store dies with the process, so a consumer that
// restarts will happily reprocess a message it had already handled — exactly the failure this
// guard exists to prevent. Each service should swap in a store backed by its own SQL or Cosmos
// context (see AddOrderFlowMessaging<TIdempotencyKeyStore>), writing the key in the same
// transaction as its business state change.

/// <summary>
/// Process-local idempotency store. Correct within a single running process, and adequate for a
/// POC where the interesting failure is redelivery rather than restart.
/// </summary>
public sealed class InMemoryIdempotencyKeyStore : IIdempotencyKeyStore
{
    private readonly ConcurrentDictionary<(string ConsumerName, Guid MessageId), byte> _processed = new();

    public Task<bool> HasProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        return Task.FromResult(_processed.ContainsKey((consumerName, messageId)));
    }

    public Task MarkProcessedAsync(string consumerName, Guid messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        _processed.TryAdd((consumerName, messageId), 0);

        return Task.CompletedTask;
    }
}
