using OrderFlow.Fulfillment.API.Managers.Data;
using OrderFlow.Fulfillment.API.Managers.ServiceModels;

namespace OrderFlow.Fulfillment.API.Managers.Extensions;

/// <summary>
/// Dead-letter records → ServiceModel. Hand-rolled assignments only — no AutoMapper, no Mapster,
/// no reflection.
/// </summary>
public static class FulfillmentMappingExtensions
{
    public static StuckDispatchServiceModel ToServiceModel(this DeadLetteredDispatch dispatch) => new()
    {
        OrderId = dispatch.OrderId,
        MessageId = dispatch.MessageId,
        Reason = dispatch.Reason,
        ErrorDescription = dispatch.ErrorDescription,
        DeliveryCount = dispatch.DeliveryCount,
        EnqueuedUtc = dispatch.EnqueuedUtc
    };

    public static IReadOnlyList<StuckDispatchServiceModel> ToServiceModels(
        this IEnumerable<DeadLetteredDispatch> dispatches) =>
        [.. dispatches.Select(ToServiceModel)];
}
