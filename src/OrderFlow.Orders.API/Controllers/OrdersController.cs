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

    // ── Ops ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Orders that have stopped moving. <b>The screen an ops lead asks for in week one.</b>
    /// </summary>
    /// <remarks>
    /// A non-terminal order that has not changed state in a good while is not slow — a healthy order
    /// crosses all five services in under a second — it is broken. Each of these is a customer waiting
    /// on something that is never coming without help. The recovery sweeper re-drives them
    /// automatically; this is how you see it happening, and how you see the ones it cannot fix.
    /// </remarks>
    [HttpGet("stuck")]
    [ProducesResponseType(typeof(IReadOnlyList<OrderServiceModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrderServiceModel>>> ListStuckAsync(CancellationToken cancellationToken)
    {
        var stuck = await orderFacade.ListStuckAsync(cancellationToken);

        return Ok(stuck);
    }

    /// <summary>
    /// One order's entire history, oldest first, straight from the event log.
    /// </summary>
    /// <remarks>
    /// The most useful endpoint in the system and very nearly free — the event store already holds
    /// every fact, in order. "Why did this order fail?" stops being an archaeology dig across five
    /// services' logs and becomes one GET: placed, reserved, charged, declined, released, failed —
    /// with the reason attached to the event that carried it.
    /// </remarks>
    [HttpGet("{id:guid}/timeline")]
    [ProducesResponseType(typeof(IReadOnlyList<OrderEventServiceModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<OrderEventServiceModel>>> GetTimelineAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var timeline = await orderFacade.GetTimelineAsync(id, cancellationToken);

        // An empty stream means no such order — unlike the active list, where empty is a real answer.
        return timeline.Count == 0 ? NotFound() : Ok(timeline);
    }

    /// <summary>
    /// Every dead-letter queue in the system, newest first.
    /// </summary>
    /// <remarks>
    /// Every row is a failure nobody was told about. A dead-lettered <c>ReleaseInventory</c> means
    /// stock is stranded; a dead-lettered <c>RefundPayment</c> means a customer is out of pocket for
    /// an order that failed. Those are the rows that cost real money, and they sit in queues that,
    /// until this endpoint existed, nothing in the system could show you.
    /// </remarks>
    [HttpGet("dead-letters")]
    [ProducesResponseType(typeof(IReadOnlyList<DeadLetterServiceModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeadLetterServiceModel>>> ListDeadLettersAsync(
        CancellationToken cancellationToken,
        [FromQuery] int maxPerSource = DefaultMaxPerSource)
    {
        // Clamped rather than trusted: it goes to the broker as a page size, and an unbounded one is
        // a free way to make this service fetch as much as the caller likes.
        var clamped = Math.Clamp(maxPerSource, 1, MaximumMaxPerSource);

        var deadLetters = await orderFacade.ListDeadLettersAsync(clamped, cancellationToken);

        return Ok(deadLetters);
    }

    /// <summary>
    /// Rebuilds the Redis read model by replaying every Cosmos event stream.
    /// </summary>
    /// <remarks>
    /// ADR-003 says Redis is a projection that can be rebuilt from the event log. This is the method
    /// that makes that true rather than aspirational: flush Redis, call this, and the ops list comes
    /// back exactly as it was. It replays through the same fold the saga uses, so the rebuilt state
    /// cannot disagree with the saga's.
    /// </remarks>
    [HttpPost("rebuild-projection")]
    [ProducesResponseType(typeof(ProjectionRebuildServiceModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProjectionRebuildServiceModel>> RebuildProjectionAsync(CancellationToken cancellationToken)
    {
        var result = await orderFacade.RebuildProjectionAsync(cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Named route rather than <c>CreatedAtAction(nameof(GetStatusAsync))</c>: MVC strips the
    /// "Async" suffix from action names by default, so the nameof would look for an action called
    /// "GetStatusAsync" that does not exist and throw at runtime.
    /// </summary>
    private const string GetStatusRouteName = "GetOrderStatus";

    private const int DefaultMaxPerSource = 20;
    private const int MaximumMaxPerSource = 100;
}
