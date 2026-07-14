using OrderFlow.Notification.API.Managers.Business;
using OrderFlow.Notification.API.Managers.ServiceModels;

namespace OrderFlow.Notification.API.Managers.Facades;

/// <summary>
/// Read-only. Notifications are sent in reaction to events off the bus and nothing else — an HTTP
/// endpoint that could send one would be a way to tell a customer something the system never decided.
/// </summary>
public interface INotificationFacade
{
    IReadOnlyList<NotificationServiceModel> ListRecent(int maxRecords);

    IReadOnlyList<NotificationServiceModel> ListForOrder(Guid orderId);
}

public class NotificationFacade(INotificationBusinessManager businessManager) : INotificationFacade
{
    public IReadOnlyList<NotificationServiceModel> ListRecent(int maxRecords) =>
        businessManager.ListRecent(maxRecords);

    public IReadOnlyList<NotificationServiceModel> ListForOrder(Guid orderId) =>
        businessManager.ListForOrder(orderId);
}

public static class NotificationFacadeExtensions
{
    public static IHostApplicationBuilder AddNotificationFacade(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<INotificationFacade, NotificationFacade>();

        return builder;
    }
}
