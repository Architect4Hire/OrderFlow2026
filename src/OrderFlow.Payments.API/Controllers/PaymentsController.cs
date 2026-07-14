using Microsoft.AspNetCore.Mvc;
using OrderFlow.Payments.API.Managers.Facades;
using OrderFlow.Payments.API.Managers.ServiceModels;

namespace OrderFlow.Payments.API.Controllers;

/// <summary>
/// The ops view's window onto payments. Read-only by design — see <see cref="IPaymentFacade"/>.
/// </summary>
/// <remarks>
/// PaymentsController, plural, so <c>[controller]</c> resolves to /api/Payments — the path the
/// client actually calls. <c>[controller]</c> strips only the "Controller" suffix, nothing else.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PaymentsController(IPaymentFacade paymentFacade) : ControllerBase
{
    /// <summary>
    /// Every payment attempt for one order. A healthy order has exactly ONE — the saga keys every
    /// charge for an order on the same idempotency key, so a second row means the guard failed and
    /// the customer was charged twice. That is the whole reason this endpoint exists.
    /// </summary>
    /// <remarks>
    /// The authorization code comes back masked. See <see cref="PaymentServiceModel"/>.
    /// </remarks>
    [HttpGet("order/{orderId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<PaymentServiceModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PaymentServiceModel>>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var payments = await paymentFacade.ListByOrderAsync(orderId, cancellationToken);

        // An order with no payments is a legitimate answer (it never got past inventory), so this is
        // an empty list, not a 404.
        return Ok(payments);
    }
}
