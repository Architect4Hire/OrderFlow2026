using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OrderFlow.Payments.API.Managers.Business;

/// <summary>The demo's decline rules. Change these to make the compensation path fire on cue.</summary>
public class PaymentOptions
{
    public const string SectionName = "Payment";

    /// <summary>
    /// Charges above this are declined. The failure-injection lever: place an order over the limit
    /// and the saga must release the inventory hold and fail the order.
    /// </summary>
    public decimal DeclineOverAmount { get; set; } = 1000m;

    /// <summary>Decline everything, regardless of amount. The blunt instrument for a live demo.</summary>
    public bool DeclineAll { get; set; }
}

/// <summary>The result of a simulated authorization. A decline is an outcome, not an exception ([R]3).</summary>
public sealed record AuthorizationOutcome(bool Approved, string AuthorizationCode, string DeclineReason)
{
    public static AuthorizationOutcome Approve(string authorizationCode) => new(true, authorizationCode, string.Empty);

    public static AuthorizationOutcome Decline(string reason) => new(false, string.Empty, reason);
}

public interface IPaymentAuthorizer
{
    Task<AuthorizationOutcome> AuthorizeAsync(
        string idempotencyKey,
        decimal amount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A payment processor that isn't. No HTTP, no SDK, no credentials, no card data ([R]2) — the
/// authorization is computed in-process and the "auth code" is a hash.
/// </summary>
/// <remarks>
/// <b>The outcome is DETERMINISTIC in the idempotency key, and that is a correctness property, not
/// a shortcut.</b> If the service crashes after inserting the Pending row but before recording the
/// outcome, the redelivered command re-authorizes — and because the same key yields the same code
/// and the same decision, the retry lands on exactly the answer the first attempt would have
/// reached. A random auth code would make that recovery path produce a different code each time,
/// so the row you end up with would depend on how many times the message happened to be redelivered.
/// A real processor gives you this same property by honouring the idempotency key server-side; this
/// simulates that contract rather than pretending it does not exist.
/// </remarks>
public class SimulatedPaymentAuthorizer(
    IOptions<PaymentOptions> options,
    ILogger<SimulatedPaymentAuthorizer> logger) : IPaymentAuthorizer
{
    public Task<AuthorizationOutcome> AuthorizeAsync(
        string idempotencyKey,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (settings.DeclineAll)
        {
            return Task.FromResult(AuthorizationOutcome.Decline("Declined: all charges are configured to decline."));
        }

        if (amount > settings.DeclineOverAmount)
        {
            return Task.FromResult(AuthorizationOutcome.Decline(
                $"Declined: amount {amount:0.00} exceeds the authorization limit of {settings.DeclineOverAmount:0.00}."));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        var authorizationCode = $"AUTH-{Convert.ToHexString(hash)[..8]}";

        // Debug only, and nowhere else (D3 [R]3). This is the closest thing to a secret the service
        // holds, and an Information-level log of it ends up in every aggregator in the estate.
        logger.LogDebug("Authorized {Amount:0.00} with {AuthorizationCode}", amount, authorizationCode);

        return Task.FromResult(AuthorizationOutcome.Approve(authorizationCode));
    }
}
