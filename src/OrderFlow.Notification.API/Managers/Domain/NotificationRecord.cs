namespace OrderFlow.Notification.API.Managers.Domain;

/// <summary>What the customer is being told.</summary>
public enum NotificationKind
{
    OrderConfirmed = 0,
    OrderFailed = 1,
    PaymentDeclined = 2
}

/// <summary>How the send ended. There is no "retrying" — by the time a record is written, it is over.</summary>
public enum NotificationStatus
{
    /// <summary>The provider accepted it.</summary>
    Sent = 0,

    /// <summary>
    /// The provider did not, and we gave up. <b>Dropped, not failed</b> — the word matters. The order
    /// is already finished and nothing about it changes because the customer did not get an email.
    /// </summary>
    Dropped = 1
}

/// <summary>
/// One attempt to tell a customer something.
/// </summary>
/// <remarks>
/// <para>
/// Named NotificationRecord, not Notification, because <c>OrderFlow.Notification.API</c> plus a type
/// called <c>Notification</c> is the same CS0118 namespace/type collision that forced Orders and
/// Payments to go plural. Renaming the type is the cheaper fix here: the AppHost and the prompt
/// library both already name this project in the singular.
/// </para>
/// <para>
/// This record exists for the demo and the ops view, and it is <b>not</b> a system of record. Nothing
/// reads it back to make a decision, nothing reconciles against it, and losing all of it would change
/// no order's outcome. That is exactly the property that lets notification be best-effort.
/// </para>
/// </remarks>
public class NotificationRecord
{
    public Guid Id { get; set; }

    /// <summary>The order this concerns. Equal to the saga's CorrelationId.</summary>
    public Guid OrderId { get; set; }

    public NotificationKind Kind { get; set; }

    public NotificationStatus Status { get; set; }

    /// <summary>What the customer was told (or would have been).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>How many times the provider was asked before we stopped asking.</summary>
    public int Attempts { get; set; }

    /// <summary>Why we gave up, when <see cref="Status"/> is Dropped.</summary>
    public string FailureReason { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}
