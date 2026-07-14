namespace OrderFlow.Fulfillment.API.Managers.ServiceModels;

/// <summary>
/// A dispatch the system gave up on, for the ops view.
/// </summary>
/// <remarks>
/// <b>Every row here is an order the saga is still waiting on and will wait on forever.</b> That is
/// what makes this screen different from a log: a hard carrier failure does NOT appear here, because
/// that order was answered (FulfillmentFailed), compensated, and closed. What lands in the DLQ is
/// the stuff nobody was told about — a poison message, a reply that could not be published, a
/// carrier that stayed unreachable past the delivery count. Money is captured, stock is held, and
/// the order will sit at Paid until a human does something. Hence the reason and the delivery count:
/// enough to diagnose and replay, not a stack trace to squint at.
/// </remarks>
public class StuckDispatchServiceModel
{
    /// <summary>The order left hanging. Paste it into the Orders view to see where it stalled.</summary>
    public Guid OrderId { get; set; }

    public string MessageId { get; set; } = string.Empty;

    /// <summary>The broker's dead-letter reason (e.g. MaxDeliveryCountExceeded, DeserializationFailed).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The detail the consumer supplied when it dead-lettered, if it did so deliberately.</summary>
    public string ErrorDescription { get; set; } = string.Empty;

    /// <summary>How many times delivery was attempted before the broker gave up.</summary>
    public int DeliveryCount { get; set; }

    public DateTimeOffset EnqueuedUtc { get; set; }
}
