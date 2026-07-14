namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Payment → saga: the charge was captured. The saga advances to DispatchFulfillment.
/// A capture is now outstanding — any later failure path MUST refund it.
/// </summary>
public record PaymentSucceeded : MessageBase
{
    /// <summary>Amount captured, rounded to 2 dp.</summary>
    public decimal Amount { get; init; }

    /// <summary>Simulated authorization code (AUTH-XXXXXXXX).</summary>
    public string AuthorizationCode { get; init; } = string.Empty;
}
