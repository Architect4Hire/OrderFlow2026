using OrderFlow.Orders.API.Managers.Domain;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.Orders.API.Managers.ViewModels;

// Domain.OrderLine and Contracts.OrderLine share a name by design — one is persisted, one is on
// the wire. Aliasing keeps both visible in this file, which is the only place that knows both.
using ContractLine = OrderFlow.Contracts.Messages.OrderLine;

namespace OrderFlow.Orders.API.Managers.Extensions;

/// <summary>
/// Hand-rolled mapping between the wire shapes and the aggregate. No AutoMapper, no Mapster, no
/// reflection: a convention-based mapper would silently start copying any property someone later
/// adds to the ViewModel — including the server-controlled ones B3 [R]1 deliberately keeps off it.
/// Here, every assignment is a line you can see in a diff.
/// </summary>
public static class OrderMappingExtensions
{
    /// <summary>
    /// Builds a new <see cref="Order"/> from a customer's request. The server assigns every
    /// identity, price, and timestamp — the ViewModel contributes only CustomerRef, Sku and
    /// Quantity (and, for now, UnitPrice — see B3).
    /// </summary>
    public static Order ToDomain(this PlaceOrderViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        // One reading of the clock for the whole aggregate, so CreatedUtc and UpdatedUtc are
        // identical on a freshly placed order rather than microseconds apart.
        var nowUtc = DateTime.UtcNow;

        var orderId = Guid.NewGuid();

        var lines = viewModel.Lines
            .Select(line => new OrderLine
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                Sku = line.Sku.Trim(),
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice
            })
            .ToList();

        var subtotal = decimal.Round(lines.Sum(line => line.Quantity * line.UnitPrice), 2);

        return new Order
        {
            Id = orderId,
            CustomerRef = viewModel.CustomerRef.Trim(),

            // The saga owns every transition after this one. Placed is the only state a mapper
            // is ever allowed to author.
            State = OrderState.Placed,

            Subtotal = subtotal,

            // TODO: pricing engine. Total == Subtotal for the POC — no tax, no shipping, no
            // discounts. The saga's compensation paths are what this demo proves, not pricing.
            Total = subtotal,

            FailureReason = string.Empty,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            Lines = lines
        };
    }

    /// <summary>Projects the aggregate to what the status view polls.</summary>
    public static OrderServiceModel ToServiceModel(this Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderServiceModel
        {
            Id = order.Id,
            CustomerRef = order.CustomerRef,

            // The name, not the ordinal — the client must not care how OrderState is numbered.
            State = order.State.ToString(),

            Subtotal = order.Subtotal,
            Total = order.Total,
            FailureReason = order.FailureReason,
            CreatedUtc = order.CreatedUtc,
            UpdatedUtc = order.UpdatedUtc,
            Lines = order.Lines
                .Select(line => new OrderLineServiceModel
                {
                    Id = line.Id,
                    Sku = line.Sku,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    LineTotal = decimal.Round(line.Quantity * line.UnitPrice, 2)
                })
                .ToList()
        };
    }

    public static List<OrderServiceModel> ToServiceModels(this IEnumerable<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);

        return orders.Select(order => order.ToServiceModel()).ToList();
    }

    /// <summary>
    /// Projects persisted lines onto the wire shape carried by ReserveInventory and
    /// DispatchFulfillment. Beyond B4's [S], but the alternative was a private copy of it in the
    /// Business layer — and mapping belongs here, where both shapes are already in scope.
    /// </summary>
    public static IReadOnlyList<ContractLine> ToMessageLines(this IEnumerable<OrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines
            .Select(line => new ContractLine
            {
                Sku = line.Sku,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice
            })
            .ToList();
    }
}
