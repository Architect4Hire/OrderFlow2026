using Azure.Messaging.ServiceBus;
using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.Saga;

namespace OrderFlow.Orders.API.Managers.Consumers;

// One consumer per event the saga subscribes to. Each is a routing entry and nothing more — the
// base class owns the idempotency guard and the settlement rules, the saga owns every decision.
// If one of these ever grows an `if`, the layering has broken.

/// <summary>inventory-reserved → the saga charges payment.</summary>
public sealed class InventoryReservedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<InventoryReservedConsumer> logger)
    : OrderSagaConsumer<InventoryReserved>(client, scopeFactory, logger)
{
    protected override Task HandleAsync(IOrderSaga saga, InventoryReserved message, CancellationToken cancellationToken)
        => saga.OnInventoryReservedAsync(message, cancellationToken);
}

/// <summary>inventory-rejected → the saga fails the order. No compensation: nothing was held.</summary>
public sealed class InventoryRejectedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<InventoryRejectedConsumer> logger)
    : OrderSagaConsumer<InventoryRejected>(client, scopeFactory, logger)
{
    protected override Task HandleAsync(IOrderSaga saga, InventoryRejected message, CancellationToken cancellationToken)
        => saga.OnInventoryRejectedAsync(message, cancellationToken);
}

/// <summary>payment-succeeded → the saga dispatches fulfillment.</summary>
public sealed class PaymentSucceededConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentSucceededConsumer> logger)
    : OrderSagaConsumer<PaymentSucceeded>(client, scopeFactory, logger)
{
    protected override Task HandleAsync(IOrderSaga saga, PaymentSucceeded message, CancellationToken cancellationToken)
        => saga.OnPaymentSucceededAsync(message, cancellationToken);
}

/// <summary>payment-declined → the saga COMPENSATES: release the inventory, then fail.</summary>
public sealed class PaymentDeclinedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentDeclinedConsumer> logger)
    : OrderSagaConsumer<PaymentDeclined>(client, scopeFactory, logger)
{
    protected override Task HandleAsync(IOrderSaga saga, PaymentDeclined message, CancellationToken cancellationToken)
        => saga.OnPaymentDeclinedAsync(message, cancellationToken);
}

/// <summary>fulfillment-dispatched → the saga confirms the order.</summary>
public sealed class FulfillmentDispatchedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<FulfillmentDispatchedConsumer> logger)
    : OrderSagaConsumer<FulfillmentDispatched>(client, scopeFactory, logger)
{
    protected override Task HandleAsync(IOrderSaga saga, FulfillmentDispatched message, CancellationToken cancellationToken)
        => saga.OnFulfillmentDispatchedAsync(message, cancellationToken);
}

/// <summary>fulfillment-failed → the saga COMPENSATES: refund AND release, then fail.</summary>
public sealed class FulfillmentFailedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<FulfillmentFailedConsumer> logger)
    : OrderSagaConsumer<FulfillmentFailed>(client, scopeFactory, logger)
{
    protected override Task HandleAsync(IOrderSaga saga, FulfillmentFailed message, CancellationToken cancellationToken)
        => saga.OnFulfillmentFailedAsync(message, cancellationToken);
}

/// <summary>Registration for all six. Called from Program.cs (Prompt B11).</summary>
public static class OrderConsumerExtensions
{
    public static IHostApplicationBuilder AddOrderConsumers(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<InventoryReservedConsumer>();
        builder.Services.AddHostedService<InventoryRejectedConsumer>();
        builder.Services.AddHostedService<PaymentSucceededConsumer>();
        builder.Services.AddHostedService<PaymentDeclinedConsumer>();
        builder.Services.AddHostedService<FulfillmentDispatchedConsumer>();
        builder.Services.AddHostedService<FulfillmentFailedConsumer>();

        return builder;
    }
}
