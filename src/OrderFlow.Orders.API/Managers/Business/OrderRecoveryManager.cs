using Microsoft.Extensions.Options;
using OrderFlow.Contracts.Messages;
using OrderFlow.Orders.API.Managers.Data;
using OrderFlow.Orders.API.Managers.Domain;
using OrderFlow.Orders.API.Managers.Extensions;
using OrderFlow.Orders.API.Managers.ServiceModels;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Orders.API.Managers.Business;

public class RecoveryOptions
{
    public const string SectionName = "Recovery";

    /// <summary>
    /// How long an order may sit in one state before it is considered stuck. Generous: a healthy
    /// order crosses all five services in well under a second, so a minute of silence is not slow,
    /// it is broken.
    /// </summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the sweeper looks.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Turn the sweeper off to demonstrate what a system without one looks like.</summary>
    public bool Enabled { get; set; } = true;
}

public interface IOrderRecoveryManager
{
    /// <summary>Orders that have stopped moving. The ops "why is this not finished" list.</summary>
    Task<IReadOnlyList<OrderServiceModel>> ListStuckAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-sends the command each stuck order is waiting on. Returns how many were re-driven.</summary>
    Task<int> RecoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds orders that have stopped moving and pushes them again.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the answer to the one failure the rest of the architecture could not survive.</b>
/// Everything driven by a message recovers on its own: a handler throws, the broker redelivers,
/// and eventually the dead-letter queue makes the failure visible. But <c>PlaceAsync</c> is driven
/// by HTTP, and it gets exactly one attempt. It appends OrderPlaced, projects the order, and then
/// sends ReserveInventory — and if that send throws, the order exists, is Placed, is in the active
/// list, and <b>nothing is listening and nothing ever will be</b>. The customer sees a 500, re-posts,
/// and gets a second order. No message was lost, so no dead-letter queue shows anything.
/// </para>
/// <para>
/// The sweeper closes that hole without an outbox: every command it re-sends carries the SAME
/// deterministic MessageId the original did, so a command that actually got through is deduped by
/// the receiver's idempotency guard and re-sending it costs nothing. A command that never got sent
/// is simply sent.
/// </para>
/// <para>
/// <b>What it deliberately does NOT do:</b> it re-sends the command; it does not re-run the saga, and
/// it never touches order state. An order stuck because its reply event dead-lettered is not fixed
/// by this and should not be — that failure is visible in the dead-letter queue and wants a human.
/// Recovery and diagnosis are different jobs, and a sweeper that quietly papered over dead-lettered
/// messages would destroy the evidence the ops view exists to show.
/// </para>
/// </remarks>
public sealed class OrderRecoveryManager(
    IOrderReadModel readModel,
    IMessageBus messageBus,
    IOptions<RecoveryOptions> options,
    ILogger<OrderRecoveryManager> logger) : IOrderRecoveryManager
{
    public async Task<IReadOnlyList<OrderServiceModel>> ListStuckAsync(CancellationToken cancellationToken = default)
    {
        var active = await readModel.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = DateTime.UtcNow - options.Value.StuckAfter;

        return [.. active.Where(order => order.UpdatedUtc < cutoff).OrderBy(order => order.UpdatedUtc)];
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var stuck = await ListStuckAsync(cancellationToken).ConfigureAwait(false);

        if (stuck.Count == 0)
        {
            return 0;
        }

        var recovered = 0;

        foreach (var order in stuck)
        {
            if (!Enum.TryParse<OrderState>(order.State, out var state))
            {
                logger.LogError("Order {OrderId} has unrecognised state '{State}'. Skipping.", order.Id, order.State);

                continue;
            }

            var command = CommandFor(order, state);

            if (command is null)
            {
                // Dispatched is the one non-terminal state with no outstanding command: the saga
                // confirms it itself, in the same handler. Nothing to re-send.
                continue;
            }

            try
            {
                await messageBus.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

                recovered++;

                logger.LogWarning(
                    "Re-drove stuck order {OrderId} ({State}, idle since {UpdatedUtc:O}) by re-sending {Command}.",
                    order.Id, state, order.UpdatedUtc, command.GetType().Name);
            }
            catch (Exception ex)
            {
                // One order failing to re-drive must not stop the rest of the sweep.
                logger.LogError(ex, "Could not re-drive stuck order {OrderId}.", order.Id);
            }
        }

        return recovered;
    }

    /// <summary>
    /// The command an order in each state is waiting on. This is the sweeper's only piece of
    /// knowledge about the workflow, and it is a mirror of the saga's, not a second opinion — if the
    /// saga's steps change, this changes with them.
    /// </summary>
    private static MessageBase? CommandFor(OrderServiceModel order, OrderState state) => state switch
    {
        OrderState.Placed => new ReserveInventory
        {
            MessageId = MessagingConventions.DeterministicMessageId(order.Id, nameof(ReserveInventory)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,
            Lines = order.Lines.ToMessageLines()
        },

        OrderState.Reserved => new ChargePayment
        {
            MessageId = MessagingConventions.DeterministicMessageId(order.Id, nameof(ChargePayment)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,
            Amount = order.Total,

            // The same key the saga used, so a re-send resolves to the payment row that already
            // exists rather than authorizing a second charge.
            IdempotencyKey = order.Id.ToString("N")
        },

        OrderState.Paid => new DispatchFulfillment
        {
            MessageId = MessagingConventions.DeterministicMessageId(order.Id, nameof(DispatchFulfillment)),
            CorrelationId = order.Id,
            OccurredUtc = DateTime.UtcNow,
            CustomerRef = order.CustomerRef,
            Lines = order.Lines.ToMessageLines()
        },

        _ => null
    };
}

/// <summary>Runs <see cref="IOrderRecoveryManager.RecoverAsync"/> on a timer.</summary>
public sealed class OrderRecoverySweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<RecoveryOptions> options,
    ILogger<OrderRecoverySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogWarning("Order recovery sweeper is DISABLED. A failed send will strand an order permanently.");

            return;
        }

        logger.LogInformation(
            "Order recovery sweeper running every {Interval}, re-driving orders idle for more than {StuckAfter}.",
            settings.SweepInterval, settings.StuckAfter);

        using var timer = new PeriodicTimer(settings.SweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                var recovery = scope.ServiceProvider.GetRequiredService<IOrderRecoveryManager>();

                var recovered = await recovery.RecoverAsync(stoppingToken).ConfigureAwait(false);

                if (recovered > 0)
                {
                    logger.LogWarning("Recovery sweep re-drove {Count} stuck order(s).", recovered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A sweeper that dies on its first bad night is worse than no sweeper, because you
                // stop watching for the thing it was supposed to catch.
                logger.LogError(ex, "Recovery sweep failed. Will try again next tick.");
            }
        }
    }
}

public static class OrderRecoveryExtensions
{
    public static IHostApplicationBuilder AddOrderRecovery(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<RecoveryOptions>(builder.Configuration.GetSection(RecoveryOptions.SectionName));

        builder.Services.AddScoped<IOrderRecoveryManager, OrderRecoveryManager>();
        builder.Services.AddHostedService<OrderRecoverySweeper>();

        return builder;
    }
}
