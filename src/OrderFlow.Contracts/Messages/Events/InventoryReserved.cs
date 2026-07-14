namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Inventory → saga: every line was held. The saga advances to ChargePayment.
/// A hold is now outstanding — any later failure path MUST release it.
/// </summary>
public record InventoryReserved : MessageBase;
