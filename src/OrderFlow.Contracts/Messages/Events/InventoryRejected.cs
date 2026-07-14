namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Inventory → saga: at least one line could not be held, and nothing remains held
/// for this order. A normal business outcome, not an error. The saga fails the order
/// with no compensation to run.
/// </summary>
public record InventoryRejected : MessageBase
{
    /// <summary>Why the reservation failed (e.g. insufficient stock for a SKU).</summary>
    public string Reason { get; init; } = string.Empty;
}
