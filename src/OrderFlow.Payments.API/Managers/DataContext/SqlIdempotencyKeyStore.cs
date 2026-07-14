using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderFlow.ServiceDefaults.Messaging;

namespace OrderFlow.Payments.API.Managers.DataContext;

/// <summary>
/// One handled message. Not a domain entity — bookkeeping for the messaging layer, which is why it
/// lives beside the DbContext rather than in Domain.
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
/// Note carefully that this is Payment's <b>second</b> idempotency guard, and the weaker of the two.
/// This one keys on the MESSAGE; the unique index on <c>Payment.IdempotencyKey</c> keys on the
/// CHARGE. A duplicate ChargePayment carrying a fresh MessageId would sail straight past this store —
/// and be stopped dead by the other one. That is the whole reason the money guard lives in the
/// Payment table and not here.
/// </para>
/// <para>
/// It still earns its place: it stops the common case (a redelivery of the identical message) before
/// it reaches the authorizer at all, and unlike the in-memory store it survives a restart.
/// </para>
/// </remarks>
public sealed class SqlIdempotencyKeyStore(
    PaymentDbContext context,
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
            logger.LogDebug(
                "{ConsumerName} had already marked message {MessageId} as processed.", consumerName, messageId);

            context.ChangeTracker.Clear();
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation };
}
