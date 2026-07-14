using Microsoft.Extensions.Options;
using OrderFlow.Notification.API.Managers.Domain;

namespace OrderFlow.Notification.API.Managers.Business;

public class NotificationOptions
{
    public const string SectionName = "Notification";

    /// <summary>Make every send fail. The "notification provider down" row of the failure matrix.</summary>
    public bool ProviderDown { get; set; }

    /// <summary>
    /// Make every send hang. Proves the timeout ([R]3) — without one, a hung provider holds the
    /// subscription's handler open and the whole notification pipeline stops behind it.
    /// </summary>
    public bool ProviderHangs { get; set; }

    /// <summary>How long a single send is allowed to take before it is abandoned.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Retries after the first attempt. Bounded, then the notification is dropped.</summary>
    public int MaxRetryAttempts { get; set; } = 2;
}

/// <summary>Raised when the simulated provider refuses a send.</summary>
public sealed class NotificationProviderException(string message) : Exception(message);

public interface INotificationProvider
{
    Task SendAsync(NotificationKind kind, Guid orderId, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// An email/SMS provider that isn't. Logs, and can be toggled to fail or hang so both failure paths
/// are demonstrable.
/// </summary>
public class SimulatedNotificationProvider(
    IOptions<NotificationOptions> options,
    ILogger<SimulatedNotificationProvider> logger) : INotificationProvider
{
    public async Task SendAsync(
        NotificationKind kind,
        Guid orderId,
        string message,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (settings.ProviderHangs)
        {
            // Longer than any sane timeout. The pipeline's timeout strategy is what rescues us — and
            // if it were missing, this line would prove it by wedging the consumer permanently.
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        }

        await Task.Delay(20, cancellationToken);

        if (settings.ProviderDown)
        {
            throw new NotificationProviderException($"Notification provider is unavailable ({kind} for order {orderId:N}).");
        }

        // The "send". In a real service this is where the SMTP or SMS call would go — and it would
        // still be best-effort, because the order is already over.
        logger.LogInformation("NOTIFY {Kind} → order {OrderId}: {Message}", kind, orderId, message);
    }
}
