using System.Text.Json;

namespace OrderFlow.Orders.API.Managers.ServiceModels;

/// <summary>
/// One entry in an order's history, straight from the event log.
/// </summary>
/// <remarks>
/// This is the artifact that makes the architecture legible. An order is not a row that mysteriously
/// changed state — it is a sequence of things that happened, in order, each with a timestamp and a
/// payload. "Why did this order fail?" stops being an archaeology exercise across five services' logs
/// and becomes one GET.
/// </remarks>
public class OrderEventServiceModel
{
    /// <summary>Position in the stream, from 1. The authoritative order of history.</summary>
    public long Sequence { get; set; }

    /// <summary>The event type: OrderPlaced, InventoryReserved, PaymentDeclined…</summary>
    public string Type { get; set; } = string.Empty;

    public DateTime OccurredUtc { get; set; }

    /// <summary>The event exactly as it happened. Whole, not summarised — this is the audit trail.</summary>
    public JsonElement Payload { get; set; }
}

/// <summary>A message the broker gave up on, anywhere in the system.</summary>
/// <remarks>
/// Every row here is a failure nobody was told about. A dead-lettered ReleaseInventory means stock is
/// stranded; a dead-lettered RefundPayment means a customer is out of pocket for an order that failed.
/// These are the rows that cost money, and until now the system could not show them.
/// </remarks>
public class DeadLetterServiceModel
{
    /// <summary>Which queue or subscription: <c>release-inventory</c>, <c>payment-declined/order-saga</c>…</summary>
    public string Source { get; set; } = string.Empty;

    public Guid OrderId { get; set; }

    public string MessageId { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string ErrorDescription { get; set; } = string.Empty;

    public int DeliveryCount { get; set; }

    public DateTimeOffset EnqueuedUtc { get; set; }
}

/// <summary>What a projection rebuild did.</summary>
public class ProjectionRebuildServiceModel
{
    public int StreamsReplayed { get; set; }

    public int OrdersProjected { get; set; }

    public DateTime CompletedUtc { get; set; }
}
