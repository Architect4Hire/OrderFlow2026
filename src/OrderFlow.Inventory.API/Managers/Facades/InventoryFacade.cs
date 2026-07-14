using OrderFlow.Inventory.API.Managers.Business;
using OrderFlow.Inventory.API.Managers.ServiceModels;

namespace OrderFlow.Inventory.API.Managers.Facades;

/// <summary>
/// The controller's only door into the service. Read-only: stock is never changed over HTTP — it
/// moves in response to saga commands off the bus and nothing else. An endpoint that could reserve
/// or release stock would be a second, unaudited way to move the numbers, sitting outside the
/// idempotency and compensation machinery entirely.
/// </summary>
public interface IInventoryFacade
{
    Task<IReadOnlyList<StockItemServiceModel>> ListStockAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReservationServiceModel>> ListReservationsAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);
}

public class InventoryFacade(IInventoryBusinessManager businessManager) : IInventoryFacade
{
    public Task<IReadOnlyList<StockItemServiceModel>> ListStockAsync(CancellationToken cancellationToken = default) =>
        businessManager.ListStockAsync(cancellationToken);

    public Task<IReadOnlyList<ReservationServiceModel>> ListReservationsAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        businessManager.ListReservationsAsync(orderId, cancellationToken);
}

public static class InventoryFacadeExtensions
{
    public static IHostApplicationBuilder AddInventoryFacade(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IInventoryFacade, InventoryFacade>();

        return builder;
    }
}
