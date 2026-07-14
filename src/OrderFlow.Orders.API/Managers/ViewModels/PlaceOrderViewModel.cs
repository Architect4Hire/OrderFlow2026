using System.ComponentModel.DataAnnotations;

namespace OrderFlow.Orders.API.Managers.ViewModels;

/// <summary>
/// Binds the customer "place order" call. Carries only what the client is allowed to assert:
/// who they are and what they want. Id, State, Subtotal and Total are absent by design — the
/// server assigns the identity, prices the order, and owns every state transition. A client that
/// could post <c>State = Confirmed</c> could confirm an order it never paid for.
/// </summary>
public class PlaceOrderViewModel
{
    [Required]
    public string CustomerRef { get; set; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = "An order must have at least one line.")]
    public List<OrderLineViewModel> Lines { get; set; } = [];
}

/// <summary>
/// A requested line: a SKU and a quantity. Validation lives here, never on the Domain entity.
/// </summary>
/// <remarks>
/// <b>There is no UnitPrice, and its absence is the security control.</b> It used to be here, and it
/// meant the caller set the price — and therefore Subtotal, Total, and the amount ChargePayment
/// authorized. A customer could buy a laptop for a penny by editing one field of the JSON they were
/// already sending, and nothing downstream would have blinked, because every service faithfully
/// carried the number it was given. Prices now come from Inventory, which owns the catalogue, and
/// arrive on InventoryReserved (ADR-006). A field that does not exist cannot be forged.
/// </remarks>
public class OrderLineViewModel
{
    [Required]
    public string Sku { get; set; } = string.Empty;

    [Range(1, 100)]
    public int Quantity { get; set; }
}
