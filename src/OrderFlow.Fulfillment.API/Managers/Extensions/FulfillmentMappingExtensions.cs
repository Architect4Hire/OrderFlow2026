using OrderFlow.Fulfillment.API.Managers.ServiceModels;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Fulfillment.API.Managers.Extensions;

/// <summary>
/// Dead-letter records → ServiceModel. Hand-rolled assignments only — no AutoMapper, no Mapster,
/// no reflection.
/// </summary>
public static class FulfillmentMappingExtensions
{
    public static StuckDispatchServiceModel ToServiceModel(this DeadLetteredMessage message) => new()
    {
        OrderId = message.OrderId,
        MessageId = message.MessageId,
        Reason = message.Reason,
        ErrorDescription = message.ErrorDescription,
        DeliveryCount = message.DeliveryCount,
        EnqueuedUtc = message.EnqueuedUtc
    };

    public static IReadOnlyList<StuckDispatchServiceModel> ToServiceModels(
        this IEnumerable<DeadLetteredMessage> messages) =>
        [.. messages.Select(ToServiceModel)];
}
