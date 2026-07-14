using Azure.Messaging.ServiceBus;
using OrderFlow.Contracts.Messages;
using OrderFlow.Payments.API.Managers.Business;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Payments.API.Managers.Consumers;

/// <summary>
/// charge-payment → charge once → answer the saga with PaymentSucceeded or PaymentDeclined.
/// </summary>
/// <remarks>
/// <para>
/// <b>Payment answers; it never orchestrates</b> ([R]1). It publishes an event and stops. It does not
/// know Fulfillment exists, and it never calls the saga back.
/// </para>
/// <para>
/// <b>There are two idempotency guards here, and they are not redundant.</b> The base class guards on
/// (ConsumerName, MessageId), which stops a redelivered ChargePayment before it ever reaches
/// Business — but the POC's store for that is in-memory, so a process restart forgets it. The guard
/// that actually protects the customer's money is the one in Business, keyed on
/// ChargePayment.IdempotencyKey and enforced by a unique index in SQL. The first is an optimisation;
/// the second is the guarantee. If you are ever tempted to delete one, delete the first.
/// </para>
/// <para>
/// <b>The publish happens inside the handler, which is what makes [R]2 hold.</b> The base completes
/// the message only after this method returns, so a failed publish leaves the command unsettled and
/// it comes back. Re-charging on that retry is safe — the idempotency key resolves to the same row
/// and returns the same outcome — and the reply carries a deterministic MessageId, so the saga drops
/// the duplicate. Settling first and then failing to publish would leave the customer charged and
/// the saga waiting forever for an answer nobody will send again.
/// </para>
/// </remarks>
public sealed class ChargePaymentConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<ChargePaymentConsumer> logger)
    : ServiceBusConsumer<ChargePayment>(client, scopeFactory, logger)
{
    /// <summary>
    /// Raised above 1 on purpose. "Duplicate payment callback" is this service's failure-matrix row,
    /// and its hardest form is two duplicates arriving at the SAME MOMENT — which a consumer pinned
    /// at 1 can never produce, so the unique-index guard that adjudicates that race would never once
    /// be exercised. Each message gets its own DI scope and its own DbContext, so this is safe.
    /// </summary>
    protected override int MaxConcurrentCalls => 4;

    protected override async Task HandleAsync(
        IServiceProvider services,
        ChargePayment command,
        CancellationToken cancellationToken)
    {
        var business = services.GetRequiredService<IPaymentBusinessManager>();
        var messageBus = services.GetRequiredService<IMessageBus>();

        var result = await business.ChargeAsync(
            command.CorrelationId,
            command.Amount,
            command.IdempotencyKey,
            cancellationToken);

        // A translation, not a decision. The decision was made in Business, and what it means for the
        // order is the saga's business, not ours.
        if (result.Captured)
        {
            await messageBus.PublishEventAsync(
                new PaymentSucceeded
                {
                    MessageId = MessagingConventions.DeterministicMessageId(command.CorrelationId, nameof(PaymentSucceeded)),
                    CorrelationId = command.CorrelationId,
                    Amount = result.Amount,
                    AuthorizationCode = result.AuthorizationCode
                },
                cancellationToken);

            return;
        }

        await messageBus.PublishEventAsync(
            new PaymentDeclined
            {
                MessageId = MessagingConventions.DeterministicMessageId(command.CorrelationId, nameof(PaymentDeclined)),
                CorrelationId = command.CorrelationId,
                Reason = result.DeclineReason
            },
            cancellationToken);
    }
}

/// <summary>
/// refund-payment → give the money back. Compensation: no reply, nobody is waiting.
/// </summary>
/// <remarks>
/// Refunding an order with nothing captured is a no-op, not an error — the expected outcome of a
/// redelivered compensation, and of an order that was declined before it ever captured anything.
/// </remarks>
public sealed class RefundPaymentConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<RefundPaymentConsumer> logger)
    : ServiceBusConsumer<RefundPayment>(client, scopeFactory, logger)
{
    protected override async Task HandleAsync(
        IServiceProvider services,
        RefundPayment command,
        CancellationToken cancellationToken)
    {
        var business = services.GetRequiredService<IPaymentBusinessManager>();

        await business.RefundAsync(command.CorrelationId, cancellationToken);
    }
}

public static class PaymentConsumerExtensions
{
    public static IHostApplicationBuilder AddPaymentConsumers(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<ChargePaymentConsumer>();
        builder.Services.AddHostedService<RefundPaymentConsumer>();

        return builder;
    }
}
