namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Order → everyone: terminal failure, all compensations issued. Notification
/// subscribes; nothing replies.
/// </summary>
public record OrderFailed : MessageBase
{
    /// <summary>Why the order failed, surfaced to the customer status view and the ops list.</summary>
    public string Reason { get; init; } = string.Empty;
}
