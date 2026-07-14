namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Saga → Payment: authorize and capture this amount for the order.
/// Answered with PaymentSucceeded or PaymentDeclined.
/// </summary>
public record ChargePayment : MessageBase
{
    /// <summary>Order total to charge, rounded to 2 dp by the producer.</summary>
    public decimal Amount { get; init; }

    /// <summary>
    /// Collapses retries and duplicate callbacks onto ONE payment row. A second
    /// ChargePayment with this key must return the first outcome, not charge again.
    /// </summary>
    public string IdempotencyKey { get; init; } = string.Empty;
}
