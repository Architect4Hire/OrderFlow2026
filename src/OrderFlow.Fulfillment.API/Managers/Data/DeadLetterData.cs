using Azure.Messaging.ServiceBus;
using OrderFlow.Contracts.Messages;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Fulfillment.API.Managers.Data;

/// <summary>A dispatch the broker gave up on, straight from the dead-letter queue.</summary>
public sealed record DeadLetteredDispatch(
    Guid OrderId,
    string MessageId,
    string Reason,
    string ErrorDescription,
    int DeliveryCount,
    DateTimeOffset EnqueuedUtc);

public interface IDeadLetterData
{
    Task<IReadOnlyList<DeadLetteredDispatch>> PeekDeadLetteredAsync(
        int maxMessages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fulfillment's only "database" is the broker's dead-letter queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>PEEK, never RECEIVE.</b> Receiving a message locks it and — settled or not — starts a clock on
/// it; a receive-based ops screen would quietly consume the very messages it exists to display, and
/// the evidence of the failure would evaporate the moment somebody looked at it. Peek is a read.
/// The DLQ stays exactly as it was, which is what lets an operator page a colleague and have them
/// see the same thing.
/// </para>
/// <para>
/// This is the whole answer to "how do I see stuck orders". Not a projection we maintain, not a
/// table we have to keep in step with reality — the broker already knows, precisely, which messages
/// it could not deliver and why it stopped trying. Rebuilding that in SQL would be duplicating a
/// source of truth for no gain.
/// </para>
/// </remarks>
public class DeadLetterData(ServiceBusClient client, ILogger<DeadLetterData> logger) : IDeadLetterData
{
    public async Task<IReadOnlyList<DeadLetteredDispatch>> PeekDeadLetteredAsync(
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        var queueName = MessagingConventions.EntityNameFor<DispatchFulfillment>();

        await using var receiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        var messages = await receiver.PeekMessagesAsync(maxMessages, cancellationToken: cancellationToken);

        logger.LogInformation("{Count} dispatch(es) sitting in the dead-letter queue.", messages.Count);

        return [.. messages.Select(ToDeadLetteredDispatch)];
    }

    private static DeadLetteredDispatch ToDeadLetteredDispatch(ServiceBusReceivedMessage message) => new(
        // The bus stamps CorrelationId with the OrderId on every send, which is exactly why this
        // screen can name the order without deserializing the body of a message that may well be
        // undeserializable — that being one of the reasons it is in here.
        Guid.TryParse(message.CorrelationId, out var orderId) ? orderId : Guid.Empty,
        message.MessageId,
        string.IsNullOrEmpty(message.DeadLetterReason) ? "Unknown" : message.DeadLetterReason,
        message.DeadLetterErrorDescription ?? string.Empty,
        message.DeliveryCount,
        message.EnqueuedTime);
}

public static class DeadLetterDataExtensions
{
    public static IHostApplicationBuilder AddFulfillmentData(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IDeadLetterData, DeadLetterData>();

        return builder;
    }
}
