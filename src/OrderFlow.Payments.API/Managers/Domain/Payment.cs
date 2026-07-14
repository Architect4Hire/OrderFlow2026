namespace OrderFlow.Payments.API.Managers.Domain;

/// <summary>Where a charge got to.</summary>
public enum PaymentStatus
{
    /// <summary>The row exists; the simulated authorization has not resolved yet.</summary>
    Pending = 0,

    /// <summary>Authorized and captured. Money is outstanding — any later failure MUST refund it.</summary>
    Captured = 1,

    /// <summary>The bank said no. A business outcome, not an error: the saga compensates and fails the order.</summary>
    Declined = 2,

    /// <summary>Compensated. The capture has been given back.</summary>
    Refunded = 3
}

/// <summary>
/// One charge against one order. <b>The idempotency row.</b>
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IdempotencyKey"/> is the whole point of this entity. The saga sets it to the order id
/// and re-sends the same value on every retry of <c>ChargePayment</c>, so a redelivered command — or
/// a duplicate provider callback — resolves to <b>this row</b> rather than authorizing a second
/// charge. That makes the key unique, and the unique index on it (configured in the DbContext, D2)
/// is not a tidiness constraint: it is the concurrency control. Two duplicate charges racing each
/// other both try to insert, the database rejects one, and the loser reads back the winner's row and
/// returns the winner's outcome. There is no row version here because there is no contended update —
/// the contention is on the INSERT.
/// </para>
/// <para>
/// <b>There is no card data on this entity, and its absence is the design</b> ([R]1). No PAN, no
/// CVV, no expiry, no cardholder name — not masked, not encrypted, not "just for the demo". This POC
/// simulates authorization, so the only things worth keeping are what was charged and the code that
/// proves it was. A field that does not exist cannot leak, cannot be logged by accident, and cannot
/// drag PCI scope into a reference architecture.
/// </para>
/// </remarks>
public class Payment
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The order being charged. Equal to the saga's CorrelationId.</summary>
    public Guid OrderId { get; set; }

    /// <summary>Amount charged, 2 dp. Rounded at the boundary where it enters, never mid-calculation.</summary>
    public decimal Amount { get; set; }

    /// <summary>Pending until the simulated authorization resolves ([R]2).</summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    /// <summary>
    /// Simulated authorization code (AUTH-XXXXXXXX). Empty until captured, and empty forever if
    /// declined. This is the closest thing to a secret the service holds: it does not leave the
    /// service unmasked, and it is never logged above Debug (D3 [R]3).
    /// </summary>
    public string AuthorizationCode { get; set; } = string.Empty;

    /// <summary>
    /// Why the bank said no. Empty unless <see cref="Status"/> is Declined.
    /// </summary>
    /// <remarks>
    /// Beyond D1's literal [S], and necessary. <c>PaymentDeclined.Reason</c> has to be re-publishable
    /// verbatim when a duplicate ChargePayment is redelivered, and the ONLY safe way to reproduce it
    /// is to have stored it. Recomputing it from the decline rule would mean re-running the rule —
    /// and if the configured threshold moved in between, the replay would produce a different reason,
    /// or approve a charge that was previously declined.
    /// </remarks>
    public string DeclineReason { get; set; } = string.Empty;

    /// <summary>
    /// What collapses retries onto one row. Supplied by the saga on <c>ChargePayment</c>, stable
    /// across every redelivery of it. Unique — see the class remarks.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>When the charge was first attempted.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When the status last moved. Pending → Captured → Refunded is a two-hop history.</summary>
    public DateTime UpdatedUtc { get; set; }
}
