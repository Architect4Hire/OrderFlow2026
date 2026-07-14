using OrderFlow.Payments.API.Managers.Business;
using OrderFlow.Payments.API.Managers.ServiceModels;

namespace OrderFlow.Payments.API.Managers.Facades;

/// <summary>
/// The controller's only door into the service, and it is <b>read-only</b>. Money moves in response
/// to saga commands off the bus and nothing else. An HTTP endpoint that could charge or refund would
/// be a second, unauthenticated path to a customer's money, sitting outside the idempotency key, the
/// unique index, and the compensation machinery entirely.
/// </summary>
public interface IPaymentFacade
{
    Task<IReadOnlyList<PaymentServiceModel>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);
}

public class PaymentFacade(IPaymentBusinessManager businessManager) : IPaymentFacade
{
    public Task<IReadOnlyList<PaymentServiceModel>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        businessManager.ListByOrderAsync(orderId, cancellationToken);
}

public static class PaymentFacadeExtensions
{
    public static IHostApplicationBuilder AddPaymentFacade(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IPaymentFacade, PaymentFacade>();

        return builder;
    }
}
