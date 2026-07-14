using Microsoft.EntityFrameworkCore;
using OrderFlow.Payments.API.Managers.Domain;

namespace OrderFlow.Payments.API.Managers.DataContext;

/// <summary>
/// The Payment store. One table, and one index that carries the entire idempotency guarantee.
/// </summary>
/// <remarks>
/// <b>The unique index on IdempotencyKey is not a tidiness constraint — it is the concurrency
/// control.</b> Inventory guards a contended UPDATE with a row version; Payment guards a contended
/// INSERT with a unique index. A read-then-insert check in C# is not enough: two duplicate charges
/// arriving at the same moment both read "no row", both decide to charge, and both insert. The
/// database is the only thing that can adjudicate that, and it does — one insert wins, the other
/// gets a unique-violation, and the loser reads back the winner's row and returns the winner's
/// outcome. Drop the index and "duplicate payment callback" stops being a no-op and starts being a
/// second charge, which is the one bug in this system that takes real money from a real customer.
/// </remarks>
public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var payment = modelBuilder.Entity<Payment>();

        payment.ToTable("Payments");
        payment.HasKey(item => item.Id);

        // ── The load-bearing line ────────────────────────────────────────────────────────────────
        payment.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
        payment.HasIndex(item => item.IdempotencyKey).IsUnique();

        // Money is decimal(18,2) explicitly. Left to convention EF picks a default precision and
        // silently truncates the scale it does not expect.
        payment.Property(item => item.Amount).HasPrecision(18, 2);

        // The enum NAME, not its number — readable in the table during a demo, and immune to
        // someone renumbering PaymentStatus later.
        payment.Property(item => item.Status).HasConversion<string>().HasMaxLength(16);

        payment.Property(item => item.AuthorizationCode).HasMaxLength(32);
        payment.Property(item => item.DeclineReason).HasMaxLength(256);

        // "Show me this order's payments" is the ops query and the refund lookup.
        payment.HasIndex(item => item.OrderId);
    }
}

public static class PaymentDbContextExtensions
{
    /// <summary>
    /// Registers the context against the AppHost's "PaymentDb" database. Aspire supplies the
    /// connection string, health check and telemetry — nothing is hard-coded here.
    /// </summary>
    public static IHostApplicationBuilder AddPaymentDataContext(this IHostApplicationBuilder builder)
    {
        builder.AddSqlServerDbContext<PaymentDbContext>("PaymentDb");

        return builder;
    }
}
