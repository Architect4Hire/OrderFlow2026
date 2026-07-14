namespace OrderFlow.Orders.API.Managers.Domain;

/// <summary>
/// The stages an order moves through. Numbered explicitly because the value is persisted and
/// projected to the read model — reordering the members would silently rewrite history.
/// </summary>
/// <remarks>
/// <see cref="Failed"/> sits at 9 rather than 5 to leave room for further happy-path stages
/// without disturbing the terminal value.
/// </remarks>
public enum OrderState
{
    Placed = 0,
    Reserved = 1,
    Paid = 2,
    Dispatched = 3,
    Confirmed = 4,
    Failed = 9
}

/// <summary>
/// The saga aggregate root. There is no separate saga-state record: an order's
/// <see cref="State"/> <em>is</em> the saga's state, and its <see cref="Id"/> is the
/// CorrelationId carried by every message the saga sends or receives.
/// </summary>
/// <remarks>
/// Deliberately anaemic. Transitions do not live here — they live in the saga (Prompt B8), so
/// that every state change, forward or compensating, happens in one auditable place. A
/// <c>Confirm()</c> method on this class would be a second, invisible one.
/// </remarks>
public class Order
{
    public Guid Id { get; set; }

    public string CustomerRef { get; set; } = string.Empty;

    public OrderState State { get; set; }

    public decimal Subtotal { get; set; }

    public decimal Total { get; set; }

    /// <summary>Populated only on the failure paths. Empty on a healthy order.</summary>
    public string FailureReason { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
}

/// <summary>
/// A line on an <see cref="Order"/>. Distinct from the contract's line type of the same name:
/// this one is internal and persisted, that one is on the wire. Prompt B4 maps between them.
/// </summary>
public class OrderLine
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }
}
