namespace OrderFlow.Orders.API.Managers.ServiceModels;

/// <summary>
/// What the status view polls. Outbound only — the client reads this, never posts it.
/// </summary>
/// <remarks>
/// No validation attributes (nothing here is ever bound from a request) and no reference to the
/// Domain model. Prompt B4's mapping extensions are the only thing that knows both shapes, which
/// is what lets the saga's persisted entity change without breaking the client.
/// </remarks>
public class OrderServiceModel
{
    public Guid Id { get; set; }

    public string CustomerRef { get; set; } = string.Empty;

    /// <summary>
    /// The order's state as a name, not the enum's ordinal. The client polls this while the saga
    /// runs, so it is the one field that must survive a renumbering of the enum unchanged.
    /// </summary>
    public string State { get; set; } = string.Empty;

    public decimal Subtotal { get; set; }

    public decimal Total { get; set; }

    /// <summary>Set only on the failure paths; empty on a healthy order.</summary>
    public string FailureReason { get; set; } = string.Empty;

    public List<OrderLineServiceModel> Lines { get; set; } = [];

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A line as the status view sees it.</summary>
public class OrderLineServiceModel
{
    public Guid Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>Derived on mapping, not stored — see B2 [R]1 on <c>Available</c>.</summary>
    public decimal LineTotal { get; set; }
}
