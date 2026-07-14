namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Saga → Inventory: COMPENSATION. Release every hold this order is carrying.
/// No reply event — fire-and-forget. Releasing an order with no active holds is a
/// valid no-op, so a redelivery is safe.
/// </summary>
public record ReleaseInventory : MessageBase
{
    /// <summary>Why the saga is unwinding (payment declined, fulfillment failed). Carried for the audit trail.</summary>
    public string Reason { get; init; } = string.Empty;
}
