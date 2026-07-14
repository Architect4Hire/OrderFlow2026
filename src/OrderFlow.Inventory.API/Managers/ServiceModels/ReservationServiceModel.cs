namespace OrderFlow.Inventory.API.Managers.ServiceModels;

/// <summary>
/// One hold, as the ops view sees it. Answers "which orders are sitting on this SKU" — the question
/// you ask the moment Available looks lower than it should.
/// </summary>
/// <remarks>
/// Beyond C1's literal [S], which asks only for availability. A bare Available number tells an
/// operator that stock is missing but not who has it, and finding the stranded reservation behind a
/// failed compensation is precisely what this POC exists to demonstrate.
/// </remarks>
public class ReservationServiceModel
{
    public Guid Id { get; set; }

    /// <summary>The order holding the stock. Paste it into the Orders view to see where it stalled.</summary>
    public Guid OrderId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    /// <summary>
    /// The enum name, not its number — a wire contract that survives someone reordering the enum,
    /// and one a human can read straight off the response. Same treatment as OrderServiceModel.State.
    /// </summary>
    public string State { get; set; } = string.Empty;
}
