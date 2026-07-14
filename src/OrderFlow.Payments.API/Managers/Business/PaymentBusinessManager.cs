using OrderFlow.Payments.API.Managers.Data;
using OrderFlow.Payments.API.Managers.Domain;
using OrderFlow.Payments.API.Managers.Extensions;
using OrderFlow.Payments.API.Managers.ServiceModels;

namespace OrderFlow.Payments.API.Managers.Business;

/// <summary>
/// The outcome of a charge. A decline is a value, not an exception ([R]3) — the saga has a
/// first-class path for it, and throwing would turn a normal business answer into a dead-lettered
/// message.
/// </summary>
public sealed record PaymentResult(bool Captured, decimal Amount, string AuthorizationCode, string DeclineReason)
{
    public static PaymentResult FromPayment(Payment payment) => new(
        payment.Status is PaymentStatus.Captured or PaymentStatus.Refunded,
        payment.Amount,
        payment.AuthorizationCode,
        payment.DeclineReason);
}

public interface IPaymentBusinessManager
{
    Task<PaymentResult> ChargeAsync(
        Guid orderId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task RefundAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentServiceModel>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Charge once, no matter how many times you are asked. This is the "duplicate payment callback"
/// row of the failure matrix, and it is the one failure in the system that costs a real customer
/// real money if it is wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guard is the database, not the if-statement.</b> Checking "does a row with this key
/// exist?" in C# and then inserting is a read-then-write race: two duplicates arriving together both
/// read "no row", both authorize, both insert. The check is still there because it is the fast path
/// and it handles the overwhelmingly common case of a redelivery arriving seconds later — but the
/// thing that makes it CORRECT is the unique index adjudicating the tie underneath it.
/// </para>
/// <para>
/// <b>A Pending row is not a resolved outcome.</b> It means someone — us, or a racer, or an attempt
/// that crashed halfway — created the row and has not recorded an answer yet. Resolving it is safe
/// only because the authorization is deterministic in the idempotency key: every party that resolves
/// the same Pending row computes the same answer, so a double-write writes the same values twice.
/// That is why the authorizer must not roll a random auth code.
/// </para>
/// </remarks>
public class PaymentBusinessManager(
    IPaymentData data,
    IPaymentAuthorizer authorizer,
    ILogger<PaymentBusinessManager> logger) : IPaymentBusinessManager
{
    public async Task<PaymentResult> ChargeAsync(
        Guid orderId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Without a key there is nothing to collapse duplicates onto, and every retry becomes a
            // fresh charge. Refusing is the only safe answer — and it throws rather than declining,
            // because this is a broken caller, not a broken card.
            throw new ArgumentException("A ChargePayment without an idempotency key cannot be made safe.", nameof(idempotencyKey));
        }

        // Rounded once, here, at the boundary where money enters the service ([R]4). Nothing
        // downstream re-rounds it.
        var chargeAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

        var payment = await data.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        if (payment is null)
        {
            payment = new Payment
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                Amount = chargeAmount,
                Status = PaymentStatus.Pending,
                IdempotencyKey = idempotencyKey,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            if (!await data.TryInsertAsync(payment, cancellationToken))
            {
                // A concurrent duplicate beat us to the key. Take its row, not ours.
                payment = await data.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Idempotency key '{idempotencyKey}' collided on insert but no row could be read back.");
            }
        }

        // [R]1. The charge already resolved — return that answer, unchanged, and do not go anywhere
        // near the authorizer. This is the whole point of the entity.
        if (payment.Status is not PaymentStatus.Pending)
        {
            logger.LogInformation(
                "Duplicate charge for order {OrderId} (key {Key}): returning the original {Status} outcome.",
                orderId, idempotencyKey, payment.Status);

            return PaymentResult.FromPayment(payment);
        }

        var authorization = await authorizer.AuthorizeAsync(payment.IdempotencyKey, payment.Amount, cancellationToken);

        payment.Status = authorization.Approved ? PaymentStatus.Captured : PaymentStatus.Declined;
        payment.AuthorizationCode = authorization.AuthorizationCode;
        payment.DeclineReason = authorization.DeclineReason;
        payment.UpdatedUtc = DateTime.UtcNow;

        await data.SaveAsync(cancellationToken);

        logger.LogInformation(
            "Charge for order {OrderId} resolved to {Status}.", orderId, payment.Status);

        return PaymentResult.FromPayment(payment);
    }

    /// <summary>
    /// Compensation. Idempotent by construction: it looks for a CAPTURED payment, so an order with
    /// no payment, a declined one, or one already refunded finds nothing and quietly does nothing.
    /// A refund that threw on "nothing to refund" would dead-letter itself the second time the saga
    /// asked — and a compensation that cannot survive being asked twice is not a compensation.
    /// </summary>
    public async Task RefundAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var payment = await data.FindCapturedByOrderAsync(orderId, cancellationToken);

        if (payment is null)
        {
            logger.LogInformation("Refund for order {OrderId}: nothing captured. No-op.", orderId);

            return;
        }

        // [B]: Status and timestamp, nothing else. Amount, OrderId and AuthorizationCode are the
        // record of WHAT was refunded — clearing the auth code here would destroy the only evidence
        // that ties this refund back to the original capture, which is precisely what an auditor
        // asks to see.
        payment.Status = PaymentStatus.Refunded;
        payment.UpdatedUtc = DateTime.UtcNow;

        await data.SaveAsync(cancellationToken);

        logger.LogInformation("Refunded {Amount:0.00} for order {OrderId}.", payment.Amount, orderId);
    }

    public async Task<IReadOnlyList<PaymentServiceModel>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        (await data.ListByOrderAsync(orderId, cancellationToken)).ToServiceModels();
}

public static class PaymentBusinessExtensions
{
    public static IHostApplicationBuilder AddPaymentBusiness(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));
        builder.Services.AddScoped<IPaymentAuthorizer, SimulatedPaymentAuthorizer>();
        builder.Services.AddScoped<IPaymentBusinessManager, PaymentBusinessManager>();

        return builder;
    }
}
