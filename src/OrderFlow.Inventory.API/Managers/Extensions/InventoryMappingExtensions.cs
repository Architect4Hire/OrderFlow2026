using OrderFlow.Inventory.API.Managers.Domain;
using OrderFlow.Inventory.API.Managers.ServiceModels;

namespace OrderFlow.Inventory.API.Managers.Extensions;

/// <summary>
/// Domain → ServiceModel. Hand-rolled assignments only — no AutoMapper, no Mapster, no reflection.
/// A mapper that silently drops a renamed field is not a saving.
/// </summary>
public static class InventoryMappingExtensions
{
    public static StockItemServiceModel ToServiceModel(this StockItem stockItem) => new()
    {
        Sku = stockItem.Sku,
        OnHand = stockItem.OnHand,
        Reserved = stockItem.Reserved,

        // Read from the domain's computed property, so the view and the reserve decision can never
        // disagree about what "available" means.
        Available = stockItem.Available,
        UpdatedUtc = stockItem.UpdatedUtc

        // RowVersion is not mapped. It is a persistence detail.
    };

    public static IReadOnlyList<StockItemServiceModel> ToServiceModels(this IEnumerable<StockItem> stockItems) =>
        [.. stockItems.Select(ToServiceModel)];

    public static ReservationServiceModel ToServiceModel(this Reservation reservation) => new()
    {
        Id = reservation.Id,
        OrderId = reservation.OrderId,
        Sku = reservation.Sku,
        Quantity = reservation.Quantity,
        State = reservation.State.ToString()
    };

    public static IReadOnlyList<ReservationServiceModel> ToServiceModels(this IEnumerable<Reservation> reservations) =>
        [.. reservations.Select(ToServiceModel)];
}
