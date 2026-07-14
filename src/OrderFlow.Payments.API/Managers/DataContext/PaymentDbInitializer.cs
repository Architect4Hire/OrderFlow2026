using Microsoft.EntityFrameworkCore;

namespace OrderFlow.Payments.API.Managers.DataContext;

/// <summary>
/// Creates the schema. No seed — unlike Inventory, Payment has nothing to pre-load: every row is
/// created by a charge.
/// </summary>
/// <remarks>
/// <c>EnsureCreated</c>, not migrations. Right for a POC whose SQL container is disposable, wrong the
/// moment the schema has to change without losing data.
/// TODO: replace with EF migrations before this is anything but a demo.
/// </remarks>
public static class PaymentDbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        // This is what creates the unique index on IdempotencyKey. Without it the service still
        // runs, still charges, and silently loses its only real protection against a double charge.
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }
}
