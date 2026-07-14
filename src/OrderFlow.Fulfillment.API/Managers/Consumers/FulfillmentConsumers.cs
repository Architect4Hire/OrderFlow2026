using Azure.Messaging.ServiceBus;
using OrderFlow.Contracts.Messages;
using OrderFlow.Fulfillment.API.Managers.Business;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Fulfillment.API.Managers.Consumers;

/// <summary>
/// dispatch-fulfillment → call the carrier → answer the saga with FulfillmentDispatched or
/// FulfillmentFailed.
/// </summary>
/// <remarks>
/// <para>
/// <b>There are two kinds of failure here and they end in opposite places. Conflating them is the
/// bug this consumer exists to avoid.</b>
/// </para>
/// <para>
/// A <b>hard carrier failure</b> — retries exhausted, or a permanent rejection — has been PROCESSED.
/// We asked, we got a final answer, and the answer was no. We publish FulfillmentFailed and complete
/// the message. The saga hears the truth ([R]2), refunds the payment, releases the stock, and fails
/// the order. Nothing is stuck and nothing belongs in the dead-letter queue: a business outcome is
/// not a delivery failure. Dead-lettering this instead of publishing would mean the saga is never
/// told anything at all — money captured, stock held, order frozen at Paid forever. That is the
/// single worst outcome available in this system, and it is exactly what "let a hard failure
/// dead-letter" would produce.
/// </para>
/// <para>
/// An <b>infrastructure failure</b> — a poison message, a publish that throws, a circuit still open
/// past the delivery count — has NOT been processed. We have no answer to give. The handler throws,
/// the base abandons the message, the broker redelivers, and after MaxDeliveryCount it dead-letters.
/// THOSE are the stuck orders, and they are what the ops endpoint lists. [R]1 is about these.
/// </para>
/// <para>
/// The publish sits inside the handler, so the base cannot complete the message until the reply is
/// away ([R]2 of C3/D3, same rule). A failed publish therefore redelivers the whole command;
/// re-dispatching is safe because the tracking reference is deterministic in the order id and the
/// reply carries a deterministic MessageId, so the saga drops the duplicate.
/// </para>
/// </remarks>
public sealed class DispatchFulfillmentConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<DispatchFulfillmentConsumer> logger)
    : ServiceBusConsumer<DispatchFulfillment>(client, scopeFactory, logger)
{
    protected override async Task HandleAsync(
        IServiceProvider services,
        DispatchFulfillment command,
        CancellationToken cancellationToken)
    {
        var business = services.GetRequiredService<IFulfillmentBusinessManager>();
        var messageBus = services.GetRequiredService<IMessageBus>();

        // A BrokenCircuitException from here is NOT caught: it escapes, the base abandons, and the
        // message comes back when the carrier is healthy again. We do not kill an order because we
        // could not reach the carrier — only because the carrier said no.
        var result = await business.DispatchAsync(
            command.CorrelationId,
            command.CustomerRef,
            command.Lines,
            cancellationToken);

        if (result.Dispatched)
        {
            await messageBus.PublishEventAsync(
                new FulfillmentDispatched
                {
                    MessageId = MessagingConventions.DeterministicMessageId(command.CorrelationId, nameof(FulfillmentDispatched)),
                    CorrelationId = command.CorrelationId,
                    TrackingRef = result.TrackingRef
                },
                cancellationToken);

            return;
        }

        // [R]2. The truth, so the saga compensates BOTH side effects: refund the payment AND release
        // the inventory hold.
        await messageBus.PublishEventAsync(
            new FulfillmentFailed
            {
                MessageId = MessagingConventions.DeterministicMessageId(command.CorrelationId, nameof(FulfillmentFailed)),
                CorrelationId = command.CorrelationId,
                Reason = result.FailureReason
            },
            cancellationToken);
    }
}

public static class FulfillmentConsumerExtensions
{
    public static IHostApplicationBuilder AddFulfillmentConsumers(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<DispatchFulfillmentConsumer>();

        return builder;
    }
}
