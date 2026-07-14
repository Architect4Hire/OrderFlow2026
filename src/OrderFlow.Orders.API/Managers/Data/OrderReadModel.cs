using System.Text.Json;
using OrderFlow.Orders.API.Managers.Domain;
using OrderFlow.Orders.API.Managers.ServiceModels;
using StackExchange.Redis;

namespace OrderFlow.Orders.API.Managers.Data;

/// <summary>
/// The light CQRS read model (ADR-003). Serves the customer status view and the ops list without
/// touching the event log on every poll.
/// </summary>
/// <remarks>
/// A projection, never the system of record. Cosmos holds what happened; this holds the current
/// answer, and it exists only so a polling UI doesn't replay an event stream every two seconds.
/// If Redis is flushed, nothing is lost that cannot be rebuilt.
/// </remarks>
public interface IOrderReadModel
{
    /// <summary>Persists the status the saga has already decided on.</summary>
    Task SetStatusAsync(OrderServiceModel order, CancellationToken cancellationToken = default);

    /// <summary>The current status, or null if this order was never projected.</summary>
    Task<OrderServiceModel?> GetStatusAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Orders that have not reached a terminal state — the ops "in flight" list.</summary>
    Task<IReadOnlyList<OrderServiceModel>> ListActiveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOrderReadModel"/>
public sealed class OrderReadModel(IConnectionMultiplexer redis, ILogger<OrderReadModel> logger) : IOrderReadModel
{
    // TODO: rebuild-from-stream. Redis is a projection and can be reconstructed by replaying each
    // order's Cosmos stream through the saga's transitions. Until that exists, a flushed Redis
    // means the ops list is empty until new orders arrive — degraded, but never wrong, because the
    // event log is untouched. Losing this store must never mean losing an order.
    private const string ActiveOrdersSetKey = "orders:active";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static string OrderKey(Guid orderId) => $"order:{orderId}";

    public async Task SetStatusAsync(OrderServiceModel order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var database = redis.GetDatabase();
        var payload = JsonSerializer.Serialize(order, SerializerOptions);

        // The state is whatever the saga already decided — this store classifies it, it never
        // computes it. Anything that isn't terminal is still in flight.
        var isTerminal = IsTerminal(order.State);

        // MULTI/EXEC: the document and the active-set membership move together. Written as two
        // separate calls, a failure between them would leave a Confirmed order sitting in the ops
        // "in flight" list forever, or an in-flight order invisible to ops.
        var transaction = database.CreateTransaction();

        _ = transaction.StringSetAsync(OrderKey(order.Id), payload);

        _ = isTerminal
            ? transaction.SetRemoveAsync(ActiveOrdersSetKey, order.Id.ToString())
            : transaction.SetAddAsync(ActiveOrdersSetKey, order.Id.ToString());

        if (!await transaction.ExecuteAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Failed to project order {order.Id} ({order.State}) into the read model.");
        }

        logger.LogInformation(
            "Projected order {OrderId} as {State} (active: {IsActive})", order.Id, order.State, !isTerminal);
    }

    public async Task<OrderServiceModel?> GetStatusAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var database = redis.GetDatabase();

        var payload = await database.StringGetAsync(OrderKey(orderId)).ConfigureAwait(false);

        // Cast to string explicitly: RedisValue converts implicitly to both string and
        // ReadOnlySpan<byte>, which makes the Deserialize overload ambiguous.
        return payload.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<OrderServiceModel>((string)payload!, SerializerOptions);
    }

    public async Task<IReadOnlyList<OrderServiceModel>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var database = redis.GetDatabase();

        var activeIds = await database.SetMembersAsync(ActiveOrdersSetKey).ConfigureAwait(false);

        if (activeIds.Length == 0)
        {
            return [];
        }

        var keys = Array.ConvertAll(activeIds, id => (RedisKey)OrderKey(Guid.Parse((string)id!)));

        // One MGET rather than N round trips — the ops view polls this.
        var payloads = await database.StringGetAsync(keys).ConfigureAwait(false);

        var orders = new List<OrderServiceModel>(payloads.Length);

        foreach (var payload in payloads)
        {
            // A member with no document means the set drifted (a flushed key, a torn write). Skip
            // it rather than fail the whole ops list for one bad entry.
            if (payload.IsNullOrEmpty)
            {
                continue;
            }

            var order = JsonSerializer.Deserialize<OrderServiceModel>((string)payload!, SerializerOptions);

            if (order is not null)
            {
                orders.Add(order);
            }
        }

        return orders;
    }

    /// <summary>
    /// Terminal = the saga is done with it. Parsed from the name rather than string-compared so a
    /// renamed state is a compile error here, not a silently-never-terminal order in the ops list.
    /// </summary>
    private static bool IsTerminal(string state) =>
        Enum.TryParse<OrderState>(state, out var parsed)
        && parsed is OrderState.Confirmed or OrderState.Failed;
}

/// <summary>Registration for the read model. Called from Program.cs (Prompt B11).</summary>
public static class OrderReadModelExtensions
{
    public static IHostApplicationBuilder AddOrderReadModel(this IHostApplicationBuilder builder)
    {
        builder.AddRedisClient("redis");

        builder.Services.AddScoped<IOrderReadModel, OrderReadModel>();

        return builder;
    }
}
