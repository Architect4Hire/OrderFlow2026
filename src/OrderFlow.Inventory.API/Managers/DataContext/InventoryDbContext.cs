using Microsoft.EntityFrameworkCore;
using OrderFlow.Inventory.API.Managers.Domain;

namespace OrderFlow.Inventory.API.Managers.DataContext;

/// <summary>
/// The Inventory relational store: stock levels and the holds taken against them.
/// </summary>
/// <remarks>
/// The one configuration that matters is <c>IsRowVersion()</c> on <see cref="StockItem.RowVersion"/>.
/// It is what turns every stock UPDATE into
/// <c>UPDATE StockItems SET Reserved = @new WHERE Sku = @sku AND RowVersion = @loaded</c> — the
/// version predicate that makes two concurrent reservations of the last unit resolve to exactly one
/// winner. Without it EF emits an unconditional UPDATE, both writers succeed, and the SKU oversells.
/// The entire concurrency story of this service rests on one line in <see cref="OnModelCreating"/>.
/// </remarks>
public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    /// <summary>The durable idempotency store. See <see cref="SqlIdempotencyKeyStore"/>.</summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var processedMessage = modelBuilder.Entity<ProcessedMessage>();

        processedMessage.ToTable("ProcessedMessages");

        // The composite key IS the guard: (ConsumerName, MessageId). Two concurrent redeliveries can
        // both read "not processed"; only one of them can insert.
        processedMessage.HasKey(item => new { item.ConsumerName, item.MessageId });
        processedMessage.Property(item => item.ConsumerName).HasMaxLength(128);

        var stockItem = modelBuilder.Entity<StockItem>();

        stockItem.ToTable("StockItems");

        // SKU is the natural key. There is no surrogate id: the bus talks in SKUs, so an integer
        // id would be a second identity for the same thing and a join for no reason.
        stockItem.HasKey(item => item.Sku);
        stockItem.Property(item => item.Sku).HasMaxLength(64);

        // Money is decimal(18,2) explicitly. Left to convention EF picks a default precision and
        // silently truncates the scale it does not expect — and this is the number the customer is
        // charged.
        stockItem.Property(item => item.UnitPrice).HasPrecision(18, 2);

        // ── The load-bearing line ────────────────────────────────────────────────────────────────
        // SQL Server stamps a fresh 8-byte value on every UPDATE; EF carries the loaded value into
        // the WHERE clause. A writer whose value is stale matches zero rows and gets
        // DbUpdateConcurrencyException instead of quietly clobbering the winner's write.
        stockItem.Property(item => item.RowVersion).IsRowVersion();

        // Derived, never stored (C1 [R]1). EF would ignore a get-only property with no backing field
        // anyway; saying so explicitly means an EF convention change can never quietly add a second
        // source of truth for how much stock exists.
        stockItem.Ignore(item => item.Available);

        var reservation = modelBuilder.Entity<Reservation>();

        reservation.ToTable("Reservations");
        reservation.HasKey(item => item.Id);
        reservation.Property(item => item.Sku).HasMaxLength(64);

        // Stored as the enum NAME, not its number: the value is readable straight out of the table
        // during a demo, and renumbering the enum can never silently reinterpret existing rows.
        reservation.Property(item => item.State)
            .HasConversion<string>()
            .HasMaxLength(16);

        // ReleaseInventory arrives with an order id and nothing else, and "find this order's live
        // holds" is the query on the compensation path. It gets the index.
        reservation.HasIndex(item => new { item.OrderId, item.State });
    }
}

public static class InventoryDbContextExtensions
{
    /// <summary>
    /// Registers the context against the AppHost's "InventoryDb" database. Aspire supplies the
    /// connection string, health check, and telemetry — nothing is hard-coded here.
    /// </summary>
    public static IHostApplicationBuilder AddInventoryDataContext(this IHostApplicationBuilder builder)
    {
        builder.AddSqlServerDbContext<InventoryDbContext>("InventoryDb");

        return builder;
    }
}
