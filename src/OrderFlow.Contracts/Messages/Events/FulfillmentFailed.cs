namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Fulfillment → saga: dispatch failed hard (retries exhausted). The saga COMPENSATES
/// BOTH outstanding side effects: refund the payment AND release the inventory hold.
/// </summary>
public record FulfillmentFailed : MessageBase
{
    /// <summary>Why dispatch failed, after retries were exhausted.</summary>
    public string Reason { get; init; } = string.Empty;
}
