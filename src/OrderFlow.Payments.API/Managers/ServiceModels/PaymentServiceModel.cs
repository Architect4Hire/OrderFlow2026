namespace OrderFlow.Payments.API.Managers.ServiceModels;

/// <summary>
/// One payment attempt, as the ops view sees it. Because the saga keys every charge for an order on
/// the same idempotency key, a healthy order has exactly ONE of these — and that is the point of
/// showing them: two rows for one order means the idempotency guard failed and the customer was
/// charged twice.
/// </summary>
public class PaymentServiceModel
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    /// <summary>Amount charged, 2 dp.</summary>
    public decimal Amount { get; set; }

    /// <summary>The enum name, not its number — readable, and immune to the enum being renumbered.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Whether a capture actually happened. The question the ops view is really asking.</summary>
    public bool IsAuthorized { get; set; }

    /// <summary>Why it was declined, if it was. Empty otherwise.</summary>
    public string DeclineReason { get; set; } = string.Empty;

    /// <summary>
    /// The authorization code with all but its last four characters starred: <c>AUTH-****4821</c>.
    /// </summary>
    /// <remarks>
    /// Beyond D1's literal [S], which says the ServiceModel exposes the attempt history without
    /// saying how much of it. D3 [R]3 forbids logging the auth code above Debug, and putting it
    /// unmasked into a browser-facing JSON response is a strictly wider exposure than the log line
    /// that restriction exists to prevent — it would sit in the browser cache, the proxy log, and
    /// anyone's DevTools tab. The last four are enough to reconcile a specific charge against a
    /// provider statement, which is the only thing an operator needs it for. If you want the full
    /// code, it should be a separate, deliberately-authorized endpoint, not a field that rides along
    /// on every list response.
    /// </remarks>
    public string AuthorizationCodeMasked { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    // IdempotencyKey is not mapped. It is the service's internal collision key, and publishing it
    // tells a caller exactly what to send to collide with an existing charge on purpose.
}
