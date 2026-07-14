using Microsoft.AspNetCore.Mvc;
using OrderFlow.Notification.API.Managers.Facades;
using OrderFlow.Notification.API.Managers.ServiceModels;

namespace OrderFlow.Notification.API.Controllers;

/// <summary>
/// The demo's proof that best-effort means what it says. Read-only — see <see cref="INotificationFacade"/>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class NotificationController(INotificationFacade notificationFacade) : ControllerBase
{
    private const int DefaultMaxRecords = 50;
    private const int MaximumMaxRecords = 200;

    /// <summary>
    /// The most recent notifications, newest first. The rows marked Dropped are the interesting
    /// ones: turn the provider off, place an order, and watch it complete perfectly while the
    /// customer is never told. That is the boundary this service exists to prove.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NotificationServiceModel>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NotificationServiceModel>> ListRecent([FromQuery] int max = DefaultMaxRecords)
    {
        var maxRecords = Math.Clamp(max, 1, MaximumMaxRecords);

        return Ok(notificationFacade.ListRecent(maxRecords));
    }

    /// <summary>What one order's customer was told, or wasn't.</summary>
    [HttpGet("order/{orderId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<NotificationServiceModel>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NotificationServiceModel>> ListForOrder(Guid orderId)
    {
        // An order with no notifications is a legitimate answer (it is still in flight), so this is
        // an empty list, not a 404.
        return Ok(notificationFacade.ListForOrder(orderId));
    }
}
