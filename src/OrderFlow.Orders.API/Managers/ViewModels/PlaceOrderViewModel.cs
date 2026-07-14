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

/// <summary>A requested line. Validation lives here, never on the Domain entity.</summary>
public class OrderLineViewModel
{
    [Required]
    public string Sku { get; set; } = string.Empty;

    [Range(1, 100)]
    public int Quantity { get; set; }

    // TODO: the server should price the line from the SKU, not trust the client's UnitPrice.
    // As it stands the caller sets the price, so the caller sets Subtotal, Total, and therefore
    // the amount ChargePayment authorizes. Fine for a POC; a real system looks the price up.
    //
    // The decimal-typed Range overload is deliberate: the (double, double) one round-trips money
    // through binary floating point and can misjudge a boundary value like 99999.99.
    [Range(typeof(decimal), "0.0", "99999.99", ParseLimitsInInvariantCulture = true)]
    public decimal UnitPrice { get; set; }
}
