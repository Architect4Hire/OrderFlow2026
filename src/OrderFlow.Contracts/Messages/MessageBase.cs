// TODO: message versioning strategy — when a payload must change, add a NEW record
// (e.g. ChargePaymentV2) rather than editing an existing one. Producers and consumers
// deploy independently, so an in-place field change breaks whoever ships second.

namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Base for every command and event on the bus. Carries the envelope fields the
/// messaging layer and the saga depend on. Data only — no behavior.
/// </summary>
public abstract record MessageBase
{
    /// <summary>Unique id for this message instance. Half of the (ConsumerName, MessageId) idempotency key.</summary>
    public Guid MessageId { get; init; }

    /// <summary>The OrderId. Set once by the originating OrderPlaced and never changed for that order.</summary>
    public Guid CorrelationId { get; init; }

    /// <summary>When the sender produced this message, UTC.</summary>
    public DateTime OccurredUtc { get; init; }
}
