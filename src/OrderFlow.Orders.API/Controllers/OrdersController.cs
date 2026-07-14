using Microsoft.AspNetCore.Mvc;
using OrderFlow.Orders.API.Managers.Facades;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.Orders.API.Managers.ViewModels;

namespace OrderFlow.Orders.API.Controllers;

/// <summary>
/// The customer view places orders here and polls for progress; the ops view reads the active list.
/// </summary>
/// <remarks>
/// Named OrdersController, not OrderController, so `[controller]` resolves to the plural route
/// /api/Orders that the client actually calls.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class OrdersController(IOrderFacade orderFacade) : ControllerBase
{
    /// <summary>
    /// Places an order and starts the saga. Returns as soon as the order is recorded and the first
    /// command is away — it does NOT wait for inventory, payment, or fulfillment. The client polls
    /// <see cref="GetStatusAsync"/> to watch it progress, which is the entire point of the
    /// architecture: a 201 here means "accepted", not "fulfilled".
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(OrderServiceModel), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OrderServiceModel>> PlaceAsync(
        [FromBody] PlaceOrderViewModel viewModel,
        CancellationToken cancellationToken)
    {
        // [ApiController] already returns 400 with the validation problem details if the ViewModel
        // fails its data annotations, so there is nothing to check here.
        var order = await orderFacade.PlaceAsync(viewModel, cancellationToken);

        return CreatedAtRoute(GetStatusRouteName, new { id = order.Id }, order);
    }

    /// <summary>Current status of one order. This is what the customer view polls.</summary>
    [HttpGet("{id:guid}", Name = GetStatusRouteName)]
    [ProducesResponseType(typeof(OrderServiceModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderServiceModel>> GetStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await orderFacade.GetStatusAsync(id, cancellationToken);

        return order is null ? NotFound() : Ok(order);
    }

    /// <summary>Orders still in flight. The ops view's "what is happening right now" list.</summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(IReadOnlyList<OrderServiceModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrderServiceModel>>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var orders = await orderFacade.ListActiveAsync(cancellationToken);

        return Ok(orders);
    }

    /// <summary>
    /// Named route rather than <c>CreatedAtAction(nameof(GetStatusAsync))</c>: MVC strips the
    /// "Async" suffix from action names by default, so the nameof would look for an action called
    /// "GetStatusAsync" that does not exist and throw at runtime.
    /// </summary>
    private const string GetStatusRouteName = "GetOrderStatus";
}
