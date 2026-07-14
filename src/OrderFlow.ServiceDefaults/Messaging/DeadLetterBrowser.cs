using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.Contracts.Messages;

namespace OrderFlow.ServiceDefaults.Messaging;

/// <summary>A queue, or a topic subscription, that can dead-letter.</summary>
public sealed record DeadLetterSource(string EntityName, string? SubscriptionName = null)
{
    /// <summary>What the ops view shows: <c>charge-payment</c>, or <c>payment-declined/order-saga</c>.</summary>
    public string DisplayName => SubscriptionName is null ? EntityName : $"{EntityName}/{SubscriptionName}";
}

/// <summary>A message the broker gave up on.</summary>
public sealed record DeadLetteredMessage(
    string Source,
    Guid OrderId,
    string MessageId,
    string MessageType,
    string Reason,
    string ErrorDescription,
    int DeliveryCount,
    DateTimeOffset EnqueuedUtc);

public interface IDeadLetterBrowser
{
    /// <summary>Everything sitting in one source's dead-letter queue.</summary>
    Task<IReadOnlyList<DeadLetteredMessage>> PeekAsync(
        DeadLetterSource source,
        int maxMessages,
        CancellationToken cancellationToken = default);

    /// <summary>Everything sitting in EVERY dead-letter queue in the system.</summary>
    Task<IReadOnlyList<DeadLetteredMessage>> PeekAllAsync(
        int maxMessagesPerSource,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The whole system's dead-letter queues, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>PEEK, never RECEIVE.</b> Receiving a message locks it and starts a clock on it; a receive-based
/// ops screen would quietly consume the very messages it exists to display, and the evidence would
/// evaporate the moment somebody looked at it. Peek is a read. Two operators can look at the same
/// thing and see the same thing.
/// </para>
/// <para>
/// <b>Why this browses EVERY entity, not just one.</b> Fulfillment's dead-letter queue was the only
/// one the system could show, which quietly implied dispatch was the only thing that could get stuck.
/// It is not, and it is not even the important one: <b>a dead-lettered ReleaseInventory is the
/// stranded-stock bug this entire architecture exists to prevent</b>, and it was invisible. So was a
/// dead-lettered RefundPayment — money taken from a customer whose order failed, with nothing on any
/// screen to say so. Those are the rows an operator most needs to see, and they live in queues nobody
/// was watching.
/// </para>
/// <para>
/// The correlation id is read off the ENVELOPE, not the body. Some of these messages are in the
/// dead-letter queue precisely because their body would not deserialize, and an ops view that fell
/// over on the poison messages would be useless exactly when it mattered.
/// </para>
/// </remarks>
public sealed class DeadLetterBrowser(ServiceBusClient client, ILogger<DeadLetterBrowser> logger) : IDeadLetterBrowser
{
    public async Task<IReadOnlyList<DeadLetteredMessage>> PeekAsync(
        DeadLetterSource source,
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var options = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter };

        await using var receiver = source.SubscriptionName is null
            ? client.CreateReceiver(source.EntityName, options)
            : client.CreateReceiver(source.EntityName, source.SubscriptionName, options);

        var messages = await receiver.PeekMessagesAsync(maxMessages, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return [.. messages.Select(message => ToDeadLetteredMessage(source, message))];
    }

    public async Task<IReadOnlyList<DeadLetteredMessage>> PeekAllAsync(
        int maxMessagesPerSource,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DeadLetteredMessage>();

        foreach (var source in MessagingTopology.DeadLetterSources)
        {
            try
            {
                results.AddRange(await PeekAsync(source, maxMessagesPerSource, cancellationToken).ConfigureAwait(false));
            }
            catch (ServiceBusException ex)
            {
                // One missing or unreachable entity must not blank the whole ops screen. The screen
                // exists for the moment things are broken; that is the worst possible time for it to
                // refuse to render.
                logger.LogError(ex, "Could not peek the dead-letter queue for {Source}.", source.DisplayName);
            }
        }

        return [.. results.OrderByDescending(message => message.EnqueuedUtc)];
    }

    private static DeadLetteredMessage ToDeadLetteredMessage(DeadLetterSource source, ServiceBusReceivedMessage message) => new(
        source.DisplayName,

        // The bus stamps CorrelationId with the OrderId on every send, which is exactly why this
        // screen can name the order without deserializing a body that may well be undeserializable.
        Guid.TryParse(message.CorrelationId, out var orderId) ? orderId : Guid.Empty,
        message.MessageId,

        // Subject carries the message type name, also stamped on the envelope by the bus.
        string.IsNullOrEmpty(message.Subject) ? "Unknown" : message.Subject,
        string.IsNullOrEmpty(message.DeadLetterReason) ? "Unknown" : message.DeadLetterReason,
        message.DeadLetterErrorDescription ?? string.Empty,
        message.DeliveryCount,
        message.EnqueuedTime);
}

/// <summary>
/// Every entity in the system that can dead-letter, derived from the contracts themselves so a
/// renamed message cannot silently drop a queue off the ops screen.
/// </summary>
public static class MessagingTopology
{
    /// <summary>Commands. One handler each, so each is a queue.</summary>
    public static IReadOnlyList<DeadLetterSource> Commands { get; } =
    [
        new(MessagingConventions.EntityNameFor<ReserveInventory>()),
        new(MessagingConventions.EntityNameFor<ReleaseInventory>()),
        new(MessagingConventions.EntityNameFor<CommitInventory>()),
        new(MessagingConventions.EntityNameFor<ChargePayment>()),
        new(MessagingConventions.EntityNameFor<RefundPayment>()),
        new(MessagingConventions.EntityNameFor<DispatchFulfillment>())
    ];

    /// <summary>Events. Each subscription has its own dead-letter queue.</summary>
    public static IReadOnlyList<DeadLetterSource> Subscriptions { get; } =
    [
        new(MessagingConventions.EntityNameFor<InventoryReserved>(), OrderSagaSubscription),
        new(MessagingConventions.EntityNameFor<InventoryRejected>(), OrderSagaSubscription),
        new(MessagingConventions.EntityNameFor<PaymentSucceeded>(), OrderSagaSubscription),
        new(MessagingConventions.EntityNameFor<PaymentDeclined>(), OrderSagaSubscription),
        new(MessagingConventions.EntityNameFor<PaymentDeclined>(), NotificationSubscription),
        new(MessagingConventions.EntityNameFor<FulfillmentDispatched>(), OrderSagaSubscription),
        new(MessagingConventions.EntityNameFor<FulfillmentFailed>(), OrderSagaSubscription),
        new(MessagingConventions.EntityNameFor<OrderConfirmed>(), NotificationSubscription),
        new(MessagingConventions.EntityNameFor<OrderFailed>(), NotificationSubscription)
    ];

    public static IReadOnlyList<DeadLetterSource> DeadLetterSources { get; } = [.. Commands, .. Subscriptions];

    public const string OrderSagaSubscription = "order-saga";
    public const string NotificationSubscription = "notification";
}

public static class DeadLetterBrowserExtensions
{
    public static IHostApplicationBuilder AddDeadLetterBrowser(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddScoped<IDeadLetterBrowser, DeadLetterBrowser>();

        return builder;
    }
}
