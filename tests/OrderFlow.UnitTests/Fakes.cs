using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.Data;
using OrderFlow.Orders.API.Managers.DataContext;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.UnitTests;

/// <summary>
/// The bus, remembered rather than sent.
/// </summary>
/// <remarks>
/// Records what was sent AND in what order, because for the compensation paths <b>the order is the
/// thing under test</b>. "Did it release the stock?" is only half the question; "did it release the
/// stock before it marked the order terminal?" is the half that actually protects the warehouse.
/// </remarks>
public sealed class FakeMessageBus : IMessageBus
{
    private readonly List<MessageBase> _sent = [];

    public IReadOnlyList<MessageBase> Sent => _sent;

    /// <summary>Set to make the next dispatch throw — the failure the saga has to survive.</summary>
    public Exception? ThrowOnDispatch { get; set; }

    public IReadOnlyList<string> SentTypeNames => [.. _sent.Select(message => message.GetType().Name)];

    public T? SentOfType<T>() where T : MessageBase => _sent.OfType<T>().FirstOrDefault();

    public int CountOf<T>() where T : MessageBase => _sent.OfType<T>().Count();

    /// <summary>Where in the sequence a message type landed. -1 if it never did.</summary>
    public int IndexOf<T>() where T : MessageBase => _sent.FindIndex(message => message is T);

    public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : MessageBase =>
        Record(command);

    public Task PublishEventAsync<T>(T @event, CancellationToken cancellationToken = default) where T : MessageBase =>
        Record(@event);

    private Task Record(MessageBase message)
    {
        if (ThrowOnDispatch is not null)
        {
            throw ThrowOnDispatch;
        }

        _sent.Add(message);

        return Task.CompletedTask;
    }
}

/// <summary>An event store in a list. Append-only, ordered, and idempotent — like the real one.</summary>
public sealed class FakeEventStore : IOrderEventStore
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<Guid, List<OrderEventEnvelope>> _streams = [];

    public IReadOnlyList<string> TypesIn(Guid orderId) =>
        _streams.TryGetValue(orderId, out var stream) ? [.. stream.Select(e => e.Type)] : [];

    public Task AppendAsync(Guid orderId, object domainEvent, CancellationToken cancellationToken = default)
    {
        if (!_streams.TryGetValue(orderId, out var stream))
        {
            stream = [];
            _streams[orderId] = stream;
        }

        var type = domainEvent.GetType().Name;

        // Mirrors the real store's idempotent append: the same event type twice is one event.
        if (stream.Any(existing => existing.Type == type))
        {
            return Task.CompletedTask;
        }

        stream.Add(new OrderEventEnvelope
        {
            Id = $"{orderId:N}-{stream.Count + 1:D4}",
            OrderId = orderId,
            Sequence = stream.Count + 1,
            Type = type,
            OccurredUtc = DateTime.UtcNow,
            Payload = JsonSerializer.SerializeToElement(domainEvent, domainEvent.GetType(), PayloadOptions)
        });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OrderEventEnvelope>> ReadStreamAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OrderEventEnvelope>>(
            _streams.TryGetValue(orderId, out var stream) ? [.. stream] : []);

    public Task<IReadOnlyList<Guid>> ListOrderIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. _streams.Keys]);
}

/// <summary>A read model in a dictionary.</summary>
public sealed class FakeReadModel : IOrderReadModel
{
    private readonly Dictionary<Guid, OrderServiceModel> _orders = [];

    public OrderServiceModel? this[Guid orderId] => _orders.GetValueOrDefault(orderId);

    public Task SetStatusAsync(OrderServiceModel order, CancellationToken cancellationToken = default)
    {
        _orders[order.Id] = order;

        return Task.CompletedTask;
    }

    public Task<OrderServiceModel?> GetStatusAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_orders.GetValueOrDefault(orderId));

    public Task<IReadOnlyList<OrderServiceModel>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OrderServiceModel>>(
            [.. _orders.Values.Where(order => order.State is not "Confirmed" and not "Failed")]);
}

/// <summary>Shorthand for the null logger, which these tests do not care about.</summary>
public static class Log
{
    public static NullLogger<T> For<T>() => NullLogger<T>.Instance;
}
