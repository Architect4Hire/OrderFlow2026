using Azure.Messaging.ServiceBus;
using OrderFlow.Contracts.Messages;
using OrderFlow.Notification.API.Managers.Business;
using OrderFlow.Notification.API.Managers.Domain;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Notification.API.Managers.Consumers;

/// <summary>
/// Base for Notification's consumers. Topic subscriptions, not queues — these are events with more
/// than one interested party, and Notification is only ever one of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Notification is a terminal subscriber: it listens and it stops there</b> ([R]1). It publishes
/// nothing. There is no reply, no acknowledgement, and no path from this service back into the
/// workflow. If you ever find yourself adding an IMessageBus here, the boundary has broken.
/// </para>
/// <para>
/// <b>These handlers never throw, and that is deliberate</b> ([R]2). Business swallows every failure
/// and records it as Dropped, so the base always completes the message. That inverts the settlement
/// rule every other consumer in OrderFlow follows — and it must, because the alternative is worse:
/// an exception here would abandon the message, redeliver it, and eventually dead-letter it, which
/// would put a failed EMAIL into the same dead-letter queue that operators watch for stranded stock
/// and un-refunded money. The order is already finished. Nothing this service does can improve it,
/// and the only thing it can do is make the real signals harder to see.
/// </para>
/// </remarks>
public abstract class NotificationConsumer<TEvent>(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger logger) : ServiceBusConsumer<TEvent>(client, scopeFactory, logger)
    where TEvent : MessageBase
{
    /// <summary>The subscription the AppHost declares for this service on each customer-facing topic.</summary>
    protected override string? SubscriptionName => "notification";

    protected abstract NotificationKind Kind { get; }

    protected abstract string MessageFor(TEvent @event);

    protected override async Task HandleAsync(
        IServiceProvider services,
        TEvent @event,
        CancellationToken cancellationToken)
    {
        var business = services.GetRequiredService<INotificationBusinessManager>();

        // Returns a record, never throws. Whatever it says, we are done.
        await business.NotifyAsync(Kind, @event.CorrelationId, MessageFor(@event), cancellationToken);
    }
}

/// <summary>order-confirmed → tell the customer their order is on its way.</summary>
public sealed class OrderConfirmedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<OrderConfirmedConsumer> logger)
    : NotificationConsumer<OrderConfirmed>(client, scopeFactory, logger)
{
    protected override NotificationKind Kind => NotificationKind.OrderConfirmed;

    protected override string MessageFor(OrderConfirmed @event) =>
        $"Your order {@event.CorrelationId:N} is confirmed and on its way.";
}

/// <summary>order-failed → tell the customer it did not happen, and why.</summary>
public sealed class OrderFailedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<OrderFailedConsumer> logger)
    : NotificationConsumer<OrderFailed>(client, scopeFactory, logger)
{
    protected override NotificationKind Kind => NotificationKind.OrderFailed;

    protected override string MessageFor(OrderFailed @event) =>
        $"We could not complete order {@event.CorrelationId:N}: {@event.Reason}. Nothing has been charged.";
}

/// <summary>
/// payment-declined → tell the customer their card was declined.
/// </summary>
/// <remarks>
/// The one topic with TWO subscribers: the saga compensates, and Notification informs. They are
/// independent, which is why the idempotency key is (ConsumerName, MessageId) and not MessageId
/// alone — keyed on the message alone, whichever of the two ran first would suppress the other.
/// </remarks>
public sealed class PaymentDeclinedConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentDeclinedConsumer> logger)
    : NotificationConsumer<PaymentDeclined>(client, scopeFactory, logger)
{
    protected override NotificationKind Kind => NotificationKind.PaymentDeclined;

    protected override string MessageFor(PaymentDeclined @event) =>
        $"Your payment for order {@event.CorrelationId:N} was declined: {@event.Reason}.";
}

public static class NotificationConsumerExtensions
{
    public static IHostApplicationBuilder AddNotificationConsumers(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<OrderConfirmedConsumer>();
        builder.Services.AddHostedService<OrderFailedConsumer>();
        builder.Services.AddHostedService<PaymentDeclinedConsumer>();

        return builder;
    }
}
