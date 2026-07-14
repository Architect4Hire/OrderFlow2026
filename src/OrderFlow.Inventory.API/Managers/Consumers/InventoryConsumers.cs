using Azure.Messaging.ServiceBus;
using OrderFlow.Contracts.Messages;
using OrderFlow.Inventory.API.Managers.Business;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Inventory.API.Managers.Consumers;

/// <summary>
/// reserve-inventory → hold the stock → answer the saga with InventoryReserved or InventoryRejected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inventory answers; it never orchestrates</b> ([R]1). It publishes an event and stops. It does
/// not know Payment exists, and it never calls the saga back. The saga decides what an answer means.
/// </para>
/// <para>
/// <b>The publish happens inside the handler, which is what makes [R]2 hold.</b> The base class
/// completes the message only after this method returns, so a failed publish throws, the command is
/// left unsettled, and it comes back. Publish-then-fail-to-settle is safe (the reply carries a
/// deterministic id, so the saga dedupes the second copy); settle-then-fail-to-publish is not — the
/// stock would be held and the saga would wait forever for an answer nobody is going to send again.
/// That asymmetry is the whole reason the order is what it is.
/// </para>
/// </remarks>
public sealed class ReserveInventoryConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<ReserveInventoryConsumer> logger)
    : ServiceBusConsumer<ReserveInventory>(client, scopeFactory, logger)
{
    /// <summary>
    /// The one consumer in the system that is deliberately NOT serialized.
    /// </summary>
    /// <remarks>
    /// Left at the default of 1, this service would handle reservations strictly one after another,
    /// two orders for the last unit would never overlap, and the row-version guard that this whole
    /// service is built around would never once fire. The concurrency demo would pass for the wrong
    /// reason. Contention is the behaviour under test, so the consumer has to be able to contend
    /// with itself. Each message gets its own DI scope and therefore its own DbContext, so this is
    /// safe as well as necessary.
    /// </remarks>
    protected override int MaxConcurrentCalls => 8;

    protected override async Task HandleAsync(
        IServiceProvider services,
        ReserveInventory command,
        CancellationToken cancellationToken)
    {
        var business = services.GetRequiredService<IInventoryBusinessManager>();
        var messageBus = services.GetRequiredService<IMessageBus>();

        var lines = command.Lines
            .Select(line => (line.Sku, line.Quantity))
            .ToList();

        var result = await business.ReserveAsync(command.CorrelationId, lines, cancellationToken);

        // The only branch in this file, and it is a translation, not a decision: the decision was
        // made in Business, and what happens next is the saga's business, not ours.
        if (result.Success)
        {
            await messageBus.PublishEventAsync(
                new InventoryReserved
                {
                    MessageId = MessagingConventions.DeterministicMessageId(command.CorrelationId, nameof(InventoryReserved)),
                    CorrelationId = command.CorrelationId
                },
                cancellationToken);

            return;
        }

        await messageBus.PublishEventAsync(
            new InventoryRejected
            {
                MessageId = MessagingConventions.DeterministicMessageId(command.CorrelationId, nameof(InventoryRejected)),
                CorrelationId = command.CorrelationId,
                Reason = result.Reason
            },
            cancellationToken);
    }
}

/// <summary>
/// release-inventory → give the stock back. Compensation: no reply, nobody is waiting.
/// </summary>
/// <remarks>
/// Releasing an order that holds nothing is a no-op, not an error ([R]3). It is the expected outcome
/// of a redelivered compensation, and of an order that was rejected before it ever took a hold. A
/// compensation that throws when it has nothing to do is a compensation that dead-letters itself out
/// of existence the second time it is asked — which is precisely how stock gets stranded.
/// </remarks>
public sealed class ReleaseInventoryConsumer(
    ServiceBusClient client,
    IServiceScopeFactory scopeFactory,
    ILogger<ReleaseInventoryConsumer> logger)
    : ServiceBusConsumer<ReleaseInventory>(client, scopeFactory, logger)
{
    protected override async Task HandleAsync(
        IServiceProvider services,
        ReleaseInventory command,
        CancellationToken cancellationToken)
    {
        var business = services.GetRequiredService<IInventoryBusinessManager>();

        await business.ReleaseAsync(command.CorrelationId, cancellationToken);
    }
}

public static class InventoryConsumerExtensions
{
    public static IHostApplicationBuilder AddInventoryConsumers(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<ReserveInventoryConsumer>();
        builder.Services.AddHostedService<ReleaseInventoryConsumer>();

        return builder;
    }
}
