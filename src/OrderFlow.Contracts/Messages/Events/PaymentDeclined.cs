namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Payment → saga: the charge was declined. A normal business outcome, not an error.
/// The saga COMPENSATES: release the inventory hold, then fail the order.
/// </summary>
public record PaymentDeclined : MessageBase
{
    /// <summary>Why the charge was declined.</summary>
    public string Reason { get; init; } = string.Empty;
}
