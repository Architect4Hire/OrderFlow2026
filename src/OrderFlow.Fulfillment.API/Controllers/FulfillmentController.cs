using Microsoft.AspNetCore.Mvc;
using OrderFlow.Fulfillment.API.Managers.Facades;
using OrderFlow.Fulfillment.API.Managers.ServiceModels;

namespace OrderFlow.Fulfillment.API.Controllers;

/// <summary>
/// The ops view's window onto stuck dispatches. Read-only by design — see <see cref="IFulfillmentFacade"/>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class FulfillmentController(IFulfillmentFacade fulfillmentFacade) : ControllerBase
{
    private const int DefaultMaxMessages = 50;
    private const int MaximumMaxMessages = 250;

    /// <summary>
    /// Dispatches the broker gave up on, with the reason it gave up. Every row is an order the saga
    /// is still waiting on and will wait on forever without a human — money captured, stock held,
    /// order frozen. Cleanly-failed orders do NOT appear here; they were answered and compensated.
    /// </summary>
    [HttpGet("stuck")]
    [ProducesResponseType(typeof(IReadOnlyList<StuckDispatchServiceModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StuckDispatchServiceModel>>> ListStuckAsync(
        CancellationToken cancellationToken,
        [FromQuery] int max = DefaultMaxMessages)
    {
        // Clamped rather than trusted: the argument goes straight to the broker as a page size, and
        // an unbounded one is a free way to make this service fetch as much as someone likes.
        var maxMessages = Math.Clamp(max, 1, MaximumMaxMessages);

        var stuck = await fulfillmentFacade.ListStuckAsync(maxMessages, cancellationToken);

        return Ok(stuck);
    }
}
