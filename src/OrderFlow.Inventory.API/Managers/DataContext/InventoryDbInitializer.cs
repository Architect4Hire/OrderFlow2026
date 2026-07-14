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
/// <c>EnsureCreated</c>, not migrations. It is the right call for a POC whose SQL container is
/// disposable, and the wrong one the moment the schema has to change without losing data.
/// TODO: replace with EF migrations before this is anything but a demo.
/// </para>
/// </remarks>
public static class InventoryDbInitializer
{
    /// <summary>
    /// SKU-LAST-1 exists for one reason: it has a single unit. Fire two concurrent orders at it and
    /// exactly one must win.
    /// </summary>
    private static readonly StockItem[] SeedStock =
    [
        new() { Sku = "SKU-LAPTOP-01", OnHand = 25 },
        new() { Sku = "SKU-MOUSE-01", OnHand = 200 },
        new() { Sku = "SKU-KEYBOARD-01", OnHand = 40 },
        new() { Sku = "SKU-MONITOR-01", OnHand = 12 },
        new() { Sku = "SKU-LAST-1", OnHand = 1 }
    ];

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<InventoryDbContext>>();

        await context.Database.EnsureCreatedAsync(cancellationToken);

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
