# Walkthrough: One SCRUB Prompt → The Code It Produced

This traces a single prompt from the OrderFlow2026 prompt library to the code it generated, so the claim "the prompt library is the source of truth" stops being abstract. The example is the **inventory reservation** — the piece of the system where the plausible, obvious implementation is silently wrong, and where a written *Restriction* is the entire difference between correct and oversold.

> **Adjust paths as needed.** File paths below follow the current solution layout. If you reorganize, update the links — the prompt-to-code mapping is the point, not the exact folder.

## Why this example

Ask any code generator to "reserve inventory" and it will write the obvious thing: read the stock level, check it's greater than zero, decrement it, save. That code compiles, passes a single-threaded test, and demos perfectly. It also lets two buyers racing for the last unit *both* succeed, because the check and the decrement aren't atomic. This is the [Plausibility Trap](https://www.architect4hire.com/scrub): correct-looking, quietly wrong, catchable only by someone who knows the domain. SCRUB defeats it by writing the restriction down before generating.

## The prompt

From the library ([`/claude`](../../claude)), lightly trimmed:

```
[S] Scope — Implement InventoryService.ReserveAsync(reservation) in the
    Inventory reacting service. Handles a ReserveStock command; publishes
    StockReserved or StockRejected.

[C] Constraints — .NET / EF Core over SQL Server. Follow the existing
    layering and the OrderFlow.Orders.* namespace convention. Reservation
    records are the SQL system-of-record (ADR-0007).

[R] Restrictions —
    Do NOT read-then-decrement in separate statements — the check and the
      write MUST be atomic, or two callers can both reserve the last unit
      (oversell). Use an atomic conditional update / optimistic concurrency.
    Do NOT throw on insufficient stock — publish StockRejected so the saga
      can compensate (ADR-0002); an exception is not a business outcome.
    Do NOT assume the command arrives once — a repeated ReserveStock for the
      same order is a no-op, not a second reservation (ADR-0003).

[U] Usage — Called by the order saga over the bus, under contention: many
    concurrent orders for the same SKU. At-least-once delivery.

[B] Behavior — Only ADD the reservation path. Existing handlers and their
    contracts compile and behave unchanged.
```

Three lines in that Restrictions block are the whole engagement's worth of judgment: atomicity (no oversell), reject-don't-throw (so compensation works), and dedupe (so retries don't double-reserve). None of them is something the model would have done unprompted.

## The code it produced

Resulting handler (see [`/src`](../../src) → Inventory service), shape shown:

```csharp
public async Task<ReservationResult> ReserveAsync(ReserveStock command)
{
    // Idempotency: a repeated command for the same order is a no-op (ADR-0003)
    if (await _reservations.ExistsAsync(command.OrderId, command.Sku))
        return ReservationResult.AlreadyReserved;

    // Atomic conditional decrement — the check and the write are one
    // statement, so concurrent callers cannot both win (ADR-0004)
    var rowsAffected = await _db.Stock
        .Where(s => s.Sku == command.Sku && s.Available >= command.Quantity)
        .ExecuteUpdateAsync(s => s.SetProperty(
            x => x.Available, x => x.Available - command.Quantity));

    if (rowsAffected == 0)
        return ReservationResult.Rejected;   // published as StockRejected, not thrown

    await _reservations.RecordAsync(command.OrderId, command.Sku, command.Quantity);
    return ReservationResult.Reserved;        // published as StockReserved
}
```

Read the code against the prompt and every restriction is visible: the existence check (dedupe), the single atomic `ExecuteUpdateAsync` guarded by `Available >= Quantity` (no oversell), and the `Rejected` result that flows back to the saga as an event instead of an exception (so compensation can run).

## The point

The interesting engineering happened *in the prompt*, before any C# existed — in deciding that reservation must be atomic, that rejection is a business event, and that the command can arrive twice. The generated code is the easy part once those decisions are written down. That's the whole thesis of AI-assisted development done with discipline: the developer's work moves to specifying the system precisely and verifying it ruthlessly, and the prompt library becomes the durable record of both.

When the generated code and a prompt later disagree — because a bug surfaces or a requirement sharpens — the prompt gets corrected too, so the library keeps describing the system that actually exists. That's why it can be trusted as a source of truth alongside the [ADRs](../adr/README.md).

---

*More on the framework: [SCRUB](https://www.architect4hire.com/scrub). Questions: <robert@architect4hire.com>.*
