using System.Collections.Concurrent;
using OrderFlow.Notification.API.Managers.Domain;

namespace OrderFlow.Notification.API.Managers.Data;

public interface INotificationStore
{
    void Add(NotificationRecord record);

    IReadOnlyList<NotificationRecord> ListRecent(int maxRecords);

    IReadOnlyList<NotificationRecord> ListForOrder(Guid orderId);
}

/// <summary>
/// A bounded in-memory ring of the most recent notifications.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-memory on purpose, and it is the one store in OrderFlow that is allowed to be.</b> Every
/// other service's state is load-bearing — lose Inventory's reservations and stock is stranded; lose
/// Payment's rows and a customer gets charged twice. Lose all of this and precisely nothing about any
/// order changes, because nothing ever reads it back to make a decision. Giving it a SQL database
/// would imply a durability guarantee that the service does not have and does not need, and would
/// quietly invite someone to start trusting it.
/// </para>
/// <para>
/// Bounded, because an unbounded in-memory list in a long-running process is a memory leak with a
/// business justification. Oldest records are evicted; this is a demo window, not an archive.
/// TODO: if notifications ever need to be auditable, they need a real store — and at that point they
/// stop being best-effort.
/// </para>
/// </remarks>
public class NotificationStore(ILogger<NotificationStore> logger) : INotificationStore
{
    private const int Capacity = 200;

    private readonly ConcurrentQueue<NotificationRecord> _records = new();

    public void Add(NotificationRecord record)
    {
        _records.Enqueue(record);

        while (_records.Count > Capacity && _records.TryDequeue(out var evicted))
        {
            logger.LogDebug("Evicted notification {Id} for order {OrderId}.", evicted.Id, evicted.OrderId);
        }
    }

    public IReadOnlyList<NotificationRecord> ListRecent(int maxRecords) =>
        [.. _records.Reverse().Take(maxRecords)];

    public IReadOnlyList<NotificationRecord> ListForOrder(Guid orderId) =>
        [.. _records.Where(record => record.OrderId == orderId).Reverse()];
}

public static class NotificationStoreExtensions
{
    public static IHostApplicationBuilder AddNotificationStore(this IHostApplicationBuilder builder)
    {
        // Singleton: the records ARE the store. A scoped one would forget everything between messages.
        builder.Services.AddSingleton<INotificationStore, NotificationStore>();

        return builder;
    }
}
