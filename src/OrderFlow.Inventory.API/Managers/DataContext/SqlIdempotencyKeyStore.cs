using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Inventory.API.Managers.DataContext;

/// <summary>
/// One handled message. Not a domain entity — it is bookkeeping for the messaging layer, which is
/// why it lives beside the DbContext rather than in Domain.
/// </summary>
public sealed class ProcessedMessage
{
    public string ConsumerName { get; set; } = string.Empty;

    public Guid MessageId { get; set; }

    public DateTime ProcessedUtc { get; set; }
}

/// <summary>
/// The durable idempotency store, in the service's own database.
/// </summary>
/// <remarks>
/// <para>
/// The composite primary key <c>(ConsumerName, MessageId)</c> is the guard. As with everything else
/// in this service, the check that matters is the one the database makes: two concurrent redeliveries
/// of the same message can both read "not processed" and both proceed, and the INSERT is what stops
/// the second one committing.
/// </para>
/// <para>
/// <b>Honest limitation.</b> The interface's ideal is that the processed key commits in the SAME
/// transaction as the state change it guards. It does not, quite: Business has already saved its
/// holds by the time the base consumer marks the message. So a crash in the gap between handling and
/// marking still replays the message. That is survivable here only because the handlers are
/// themselves idempotent — ReserveAsync resets, ReleaseAsync no-ops on an already-released order.
/// Closing the gap properly means letting the consumer own the transaction, which is a bigger change
/// than this store.
/// </para>
/// </remarks>
public sealed class SqlIdempotencyKeyStore(
    InventoryDbContext context,
    ILogger<SqlIdempotencyKeyStore> logger) : IIdempotencyKeyStore
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public Task<bool> HasProcessedAsync(
        string consumerName,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        return context.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(item => item.ConsumerName == consumerName && item.MessageId == messageId, cancellationToken);
    }

    public async Task MarkProcessedAsync(
        string consumerName,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        context.ProcessedMessages.Add(new ProcessedMessage
        {
            ConsumerName = consumerName,
            MessageId = messageId,
            ProcessedUtc = DateTime.UtcNow
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent redelivery marked it first. Marking twice is as good as marking once.
            logger.LogDebug(
                "{ConsumerName} had already marked message {MessageId} as processed.", consumerName, messageId);

            context.ChangeTracker.Clear();
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation };
}
