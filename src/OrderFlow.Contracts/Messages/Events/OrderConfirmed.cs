namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Order → everyone: terminal success. Notification subscribes; nothing replies.
/// </summary>
public record OrderConfirmed : MessageBase;
