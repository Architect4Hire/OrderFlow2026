using Microsoft.EntityFrameworkCore;

namespace OrderFlow.Payments.API.Managers.DataContext;

/// <summary>
/// Creates the schema. No seed — unlike Inventory, Payment has nothing to pre-load: every row is
/// created by a charge.
/// </summary>
/// <remarks>
/// <b>Migrations, not EnsureCreated.</b> SQL runs on a persistent volume, so the database outlives
/// the container — and EnsureCreated only ever creates a schema that is absent. The first schema
/// change would leave every existing database silently on the old shape, and the service would fail
/// at runtime on a column nobody can find. Migrate() applies the delta to whatever is already there.
/// </remarks>
public static class PaymentDbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        // This is what creates the unique index on IdempotencyKey. Without it the service still
        // runs, still charges, and silently loses its only real protection against a double charge.
        await context.Database.MigrateAsync(cancellationToken);
    }
}
