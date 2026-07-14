using Microsoft.EntityFrameworkCore;
using OrderFlow.Inventory.API.Managers.Domain;

namespace OrderFlow.Inventory.API.Managers.DataContext;

/// <summary>
/// Creates the schema and puts stock on the shelves, so the service has something to contend over.
/// </summary>
/// <remarks>
/// <para>
/// Beyond C3's literal [S], and necessary: without it the tables do not exist and the first query
/// fails, and even with tables there would be zero SKUs — the "concurrent purchase of the last unit"
/// demo would have no last unit.
/// </para>
/// <para>
/// <b>Migrations, not EnsureCreated.</b> The AppHost gives SQL a PERSISTENT volume — deliberately, so
/// stock levels survive a restart and the concurrency demo has real history to contend over. That is
/// exactly the situation EnsureCreated cannot survive: it creates the schema only when the database
/// does not exist, so the first time a column is added, every existing database silently keeps the
/// old shape and the service fails at runtime on a column nobody can find. Migrate() applies the
/// delta to whatever is already there.
/// </para>
/// </remarks>
public static class InventoryDbInitializer
{
    /// <summary>
    /// SKU-LAST-1 exists for one reason: it has a single unit. Fire two concurrent orders at it and
    /// exactly one must win.
    /// </summary>
    /// <remarks>
    /// The prices are the CATALOGUE, and the catalogue lives here because Inventory owns it. Note
    /// SKU-LAPTOP-01 at 1299.99: it is above the payment service's default decline threshold of 1000,
    /// so ordering one is the shortest path to watching the compensation fire — the charge is
    /// declined, the hold is released, the order fails, and the customer is told. Two demos for the
    /// price of one seed row.
    /// </remarks>
    private static readonly StockItem[] SeedStock =
    [
        new() { Sku = "SKU-LAPTOP-01", OnHand = 25, UnitPrice = 1299.99m },
        new() { Sku = "SKU-MOUSE-01", OnHand = 200, UnitPrice = 24.50m },
        new() { Sku = "SKU-KEYBOARD-01", OnHand = 40, UnitPrice = 79.00m },
        new() { Sku = "SKU-MONITOR-01", OnHand = 12, UnitPrice = 249.99m },
        new() { Sku = "SKU-LAST-1", OnHand = 1, UnitPrice = 9.99m }
    ];

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<InventoryDbContext>>();

        await context.Database.MigrateAsync(cancellationToken);

        // Seed once. The AppHost gives SQL a persistent volume precisely so stock levels survive a
        // restart — re-seeding on every boot would wipe out exactly the history the demo depends on.
        if (await context.StockItems.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Inventory already seeded.");

            return;
        }

        var now = DateTime.UtcNow;

        foreach (var item in SeedStock)
        {
            item.UpdatedUtc = now;
        }

        context.StockItems.AddRange(SeedStock);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded {Count} SKUs.", SeedStock.Length);
    }
}
