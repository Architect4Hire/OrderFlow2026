namespace OrderFlow.Notification.API.Managers.ServiceModels;

/// <summary>
/// One notification, as the ops/demo view sees it. A row with Status = Dropped is the point of the
/// screen: it shows a customer who was not told, next to an order that completed perfectly anyway.
/// </summary>
public class NotificationServiceModel
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    /// <summary>Enum name, not number.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Sent, or Dropped.</summary>
    public string Status { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public string FailureReason { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}
