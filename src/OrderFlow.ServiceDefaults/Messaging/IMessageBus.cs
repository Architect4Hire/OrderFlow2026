using OrderFlow.Contracts.Messages;

namespace OrderFlow.ServiceDefaults.Messaging;

/// <summary>
/// The single seam every service publishes through. Correlation and MessageId stamping live
/// behind this interface so no individual producer can forget them.
/// </summary>
/// <remarks>
/// Commands go to a queue (exactly one handler); events go to a topic (any number of
/// subscribers). Both resolve their entity name from the message type — <c>ReserveInventory</c>
/// sends to the <c>reserve-inventory</c> queue, <c>InventoryReserved</c> publishes to the
/// <c>inventory-reserved</c> topic. That kebab-case convention is the contract between
/// OrderFlow.Contracts and the entities declared in the AppHost.
/// </remarks>
public interface IMessageBus
{
    /// <summary>Sends a command to its queue. Exactly one consumer will handle it.</summary>
    Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : MessageBase;

    /// <summary>Publishes an event to its topic. Every subscriber gets a copy.</summary>
    Task PublishEventAsync<T>(T @event, CancellationToken cancellationToken = default) where T : MessageBase;
}
