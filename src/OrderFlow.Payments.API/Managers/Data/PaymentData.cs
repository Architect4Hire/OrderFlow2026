using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Payments.API.Managers.DataContext;
using OrderFlow.Payments.API.Managers.Domain;

namespace OrderFlow.Payments.API.Managers.Data;

public interface IPaymentData
{
    /// <summary>The idempotency lookup. Tracked — the caller may be about to resolve this row.</summary>
    Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>The refund lookup: this order's captured payment, if it has one.</summary>
    Task<Payment?> FindCapturedByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert the row, letting the database adjudicate the race. <c>false</c> means someone else
    /// inserted this idempotency key first — not an error, the expected outcome of a duplicate.
    /// </summary>
    Task<bool> TryInsertAsync(Payment payment, CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Payment>> ListByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
}

public class PaymentData(PaymentDbContext context, ILogger<PaymentData> logger) : IPaymentData
{
    /// <summary>SQL Server: 2627 = unique constraint, 2601 = unique index. Both mean "already taken".</summary>
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public async Task<Payment?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await context.Payments
            .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<Payment?> FindCapturedByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        await context.Payments
            .FirstOrDefaultAsync(
                item => item.OrderId == orderId && item.Status == PaymentStatus.Captured,
                cancellationToken);

    public async Task<bool> TryInsertAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        context.Payments.Add(payment);

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the insert race. This is the duplicate-callback path working exactly as designed,
            // so it is information, not an error.
            logger.LogInformation(
                "Idempotency key {Key} was already inserted by a concurrent charge for order {OrderId}. Deferring to it.",
                payment.IdempotencyKey, payment.OrderId);

            // Drop our losing insert from the change tracker, or the next SaveChanges retries it and
            // throws again.
            context.Entry(payment).State = EntityState.Detached;

            return false;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<Payment>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        await context.Payments
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .OrderBy(item => item.CreatedUtc)
            .ToListAsync(cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation };
}

public static class PaymentDataExtensions
{
    public static IHostApplicationBuilder AddPaymentData(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IPaymentData, PaymentData>();

        return builder;
    }
}
