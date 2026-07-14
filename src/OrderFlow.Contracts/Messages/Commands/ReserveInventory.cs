namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Saga → Inventory: hold stock for every line of this order, all-or-nothing.
/// Answered with InventoryReserved or InventoryRejected.
/// </summary>
public record ReserveInventory : MessageBase
{
    /// <summary>The lines to hold. Reservation succeeds only if every line can be satisfied.</summary>
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];
}
