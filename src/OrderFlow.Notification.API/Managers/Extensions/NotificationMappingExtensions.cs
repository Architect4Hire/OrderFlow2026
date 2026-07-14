using OrderFlow.Notification.API.Managers.Domain;
using OrderFlow.Notification.API.Managers.ServiceModels;

namespace OrderFlow.Notification.API.Managers.Extensions;

/// <summary>
/// Domain → ServiceModel. Hand-rolled assignments only — no AutoMapper, no Mapster, no reflection.
/// </summary>
public static class NotificationMappingExtensions
{
    public static NotificationServiceModel ToServiceModel(this NotificationRecord record) => new()
    {
        Id = record.Id,
        OrderId = record.OrderId,
        Kind = record.Kind.ToString(),
        Status = record.Status.ToString(),
        Message = record.Message,
        Attempts = record.Attempts,
        FailureReason = record.FailureReason,
        CreatedUtc = record.CreatedUtc
    };

    public static IReadOnlyList<NotificationServiceModel> ToServiceModels(
        this IEnumerable<NotificationRecord> records) =>
        [.. records.Select(ToServiceModel)];
}
