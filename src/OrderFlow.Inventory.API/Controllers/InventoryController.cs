using Microsoft.AspNetCore.Mvc;
using OrderFlow.Inventory.API.Managers.Facades;
using OrderFlow.Inventory.API.Managers.ServiceModels;

namespace OrderFlow.Inventory.API.Controllers;

/// <summary>
/// The ops view's window onto stock. Read-only by design — see <see cref="IInventoryFacade"/>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class InventoryController(IInventoryFacade inventoryFacade) : ControllerBase
{
    /// <summary>
    /// Availability per SKU. The number to watch during the concurrency demo: Reserved climbs as
    /// orders take holds, and drops back as compensations release them. A gap that never closes is a
    /// compensation that never ran.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<StockItemServiceModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StockItemServiceModel>>> ListStockAsync(CancellationToken cancellationToken)
    {
        var stock = await inventoryFacade.ListStockAsync(cancellationToken);

        return Ok(stock);
    }

    /// <summary>
    /// The holds one order is carrying, Released ones included. This is how you answer "who is
    /// sitting on that stock" when Available looks wrong.
    /// </summary>
    [HttpGet("reservations/{orderId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<ReservationServiceModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReservationServiceModel>>> ListReservationsAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var reservations = await inventoryFacade.ListReservationsAsync(orderId, cancellationToken);

        return Ok(reservations);
    }
}
