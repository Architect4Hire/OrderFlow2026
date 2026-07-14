using OrderFlow.Fulfillment.API.Managers.Business;
using OrderFlow.Fulfillment.API.Managers.ServiceModels;

namespace OrderFlow.Fulfillment.API.Managers.Facades;

/// <summary>
/// The controller's only door in, and it is read-only. Dispatch happens in response to a saga
/// command off the bus and nothing else — an HTTP endpoint that could dispatch would let someone
/// ship an order the saga never paid for.
/// </summary>
public interface IFulfillmentFacade
{
    Task<IReadOnlyList<StuckDispatchServiceModel>> ListStuckAsync(
        int maxMessages,
        CancellationToken cancellationToken = default);
}

public class FulfillmentFacade(IFulfillmentBusinessManager businessManager) : IFulfillmentFacade
{
    public Task<IReadOnlyList<StuckDispatchServiceModel>> ListStuckAsync(
        int maxMessages,
        CancellationToken cancellationToken = default) =>
        businessManager.ListStuckAsync(maxMessages, cancellationToken);
}

public static class FulfillmentFacadeExtensions
{
    public static IHostApplicationBuilder AddFulfillmentFacade(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IFulfillmentFacade, FulfillmentFacade>();

        return builder;
    }
}
