using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderFlow.ServiceDefaults.Messaging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers the messaging seam. Called from every service's Program.cs alongside
/// <c>AddServiceDefaults()</c>.
/// </summary>
public static class MessagingExtensions
{
    /// <summary>Matches the Service Bus resource name declared in the AppHost.</summary>
    public const string DefaultConnectionName = "servicebus";

    /// <summary>
    /// Registers the Service Bus client, the bus, and a process-local idempotency store.
    /// </summary>
    public static IHostApplicationBuilder AddOrderFlowMessaging(
        this IHostApplicationBuilder builder,
        string connectionName = DefaultConnectionName)
    {
        builder.AddMessageBus(connectionName);

        builder.Services.TryAddSingleton<IIdempotencyKeyStore, InMemoryIdempotencyKeyStore>();

        return builder;
    }

    /// <summary>
    /// Registers the Service Bus client, the bus, and a service-supplied durable idempotency
    /// store — one backed by that service's own SQL or Cosmos context, so the processed key
    /// commits in the same transaction as the state change it guards.
    /// </summary>
    public static IHostApplicationBuilder AddOrderFlowMessaging<TIdempotencyKeyStore>(
        this IHostApplicationBuilder builder,
        string connectionName = DefaultConnectionName)
        where TIdempotencyKeyStore : class, IIdempotencyKeyStore
    {
        builder.AddMessageBus(connectionName);

        // Scoped, because a durable store shares the consumer's DbContext.
        builder.Services.TryAddScoped<IIdempotencyKeyStore, TIdempotencyKeyStore>();

        return builder;
    }

    private static void AddMessageBus(this IHostApplicationBuilder builder, string connectionName)
    {
        // Aspire hands us the connection string from the AppHost resource reference — nothing
        // to hard-code, and the emulator and a live namespace look identical from here.
        builder.AddAzureServiceBusClient(connectionName);

        // Singleton: it caches one sender per entity, and ServiceBusSender is thread-safe.
        builder.Services.TryAddSingleton<IMessageBus, ServiceBusMessageBus>();
    }
}
