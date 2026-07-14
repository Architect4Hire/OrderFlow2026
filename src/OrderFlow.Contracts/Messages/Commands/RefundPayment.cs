namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Saga → Payment: COMPENSATION. Refund the capture taken for this order.
/// No reply event — fire-and-forget. Refunding an already-refunded order is a no-op.
/// </summary>
public record RefundPayment : MessageBase
{
    /// <summary>Why the saga is unwinding. Carried for the audit trail.</summary>
    public string Reason { get; init; } = string.Empty;
}
