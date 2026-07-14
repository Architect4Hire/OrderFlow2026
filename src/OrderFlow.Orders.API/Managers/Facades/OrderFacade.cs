using OrderFlow.Orders.API.Managers.Business;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.Orders.API.Managers.ViewModels;

namespace OrderFlow.Orders.API.Managers.Facades;

/// <summary>The only thing the Controller depends on.</summary>
public interface IOrderFacade
{
    Task<OrderServiceModel> PlaceAsync(PlaceOrderViewModel viewModel, CancellationToken cancellationToken = default);

    Task<OrderServiceModel?> GetStatusAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderServiceModel>> ListActiveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOrderFacade"/>
/// <remarks>
/// Deliberately a pass-through. Its value is the dependency it DOESN'T have: the Controller can
/// see this and nothing below it, so no Cosmos, Redis, or Service Bus type can reach the HTTP
/// layer. The day this file grows a decision in it, the layering has already broken.
/// </remarks>
public sealed class OrderFacade(IOrderBusinessManager businessManager) : IOrderFacade
{
    public Task<OrderServiceModel> PlaceAsync(PlaceOrderViewModel viewModel, CancellationToken cancellationToken = default)
        => businessManager.PlaceAsync(viewModel, cancellationToken);

    public Task<OrderServiceModel?> GetStatusAsync(Guid orderId, CancellationToken cancellationToken = default)
        => businessManager.GetStatusAsync(orderId, cancellationToken);

    public Task<IReadOnlyList<OrderServiceModel>> ListActiveAsync(CancellationToken cancellationToken = default)
        => businessManager.ListActiveAsync(cancellationToken);
}
