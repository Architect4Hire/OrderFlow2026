using Microsoft.Extensions.Options;
using OrderFlow.Payments.API.Managers.Business;
using OrderFlow.Payments.API.Managers.Data;
using OrderFlow.Payments.API.Managers.Domain;

namespace OrderFlow.UnitTests;

/// <summary>
/// A fake payment table. Enforces the unique idempotency key, because that constraint IS the
/// duplicate-charge guard — a fake that let two rows share a key would test nothing.
/// </summary>
public sealed class FakePaymentData : IPaymentData
{
    private readonly List<Payment> _payments = [];

    public int Count => _payments.Count;

    public int AuthorizeCalls { get; set; }

    public Task<Payment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_payments.FirstOrDefault(p => p.IdempotencyKey == idempotencyKey));

    public Task<Payment?> FindCapturedByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_payments.FirstOrDefault(p => p.OrderId == orderId && p.Status == PaymentStatus.Captured));

    public Task<bool> TryInsertAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        // The unique index, in a list. Returning false is the database rejecting the second insert.
        if (_payments.Any(p => p.IdempotencyKey == payment.IdempotencyKey))
        {
            return Task.FromResult(false);
        }

        _payments.Add(payment);

        return Task.FromResult(true);
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Payment>> ListByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Payment>>([.. _payments.Where(p => p.OrderId == orderId)]);
}

/// <summary>Counts how many times the "processor" was actually asked.</summary>
public sealed class CountingAuthorizer(IPaymentAuthorizer inner) : IPaymentAuthorizer
{
    public int Calls { get; private set; }

    public Task<AuthorizationOutcome> AuthorizeAsync(string idempotencyKey, decimal amount, CancellationToken cancellationToken = default)
    {
        Calls++;

        return inner.AuthorizeAsync(idempotencyKey, amount, cancellationToken);
    }
}

public class PaymentBusinessTests
{
    private readonly FakePaymentData _data = new();
    private readonly Guid _orderId = Guid.NewGuid();

    private string Key => _orderId.ToString("N");

    private (PaymentBusinessManager Business, CountingAuthorizer Authorizer) Create(PaymentOptions? options = null)
    {
        var authorizer = new CountingAuthorizer(
            new SimulatedPaymentAuthorizer(
                Options.Create(options ?? new PaymentOptions()),
                Log.For<SimulatedPaymentAuthorizer>()));

        return (new PaymentBusinessManager(_data, authorizer, Log.For<PaymentBusinessManager>()), authorizer);
    }

    // ── The duplicate-callback row of the failure matrix ─────────────────────────────────────────

    [Fact]
    public async Task A_duplicate_charge_returns_the_first_outcome_and_never_asks_the_processor_again()
    {
        var (business, authorizer) = Create();

        var first = await business.ChargeAsync(_orderId, 100m, Key);
        var second = await business.ChargeAsync(_orderId, 100m, Key);

        // One row, one authorization, the same answer twice. This is the single most expensive thing
        // in the system to get wrong: a second charge is money taken from a real customer.
        Assert.Single([.. Enumerable.Range(0, _data.Count)]);
        Assert.Equal(1, _data.Count);
        Assert.Equal(1, authorizer.Calls);
        Assert.Equal(first.AuthorizationCode, second.AuthorizationCode);
        Assert.True(second.Captured);
    }

    [Fact]
    public async Task A_duplicate_charge_after_a_DECLINE_returns_the_decline_and_its_reason()
    {
        var (business, authorizer) = Create(new PaymentOptions { DeclineAll = true });

        var first = await business.ChargeAsync(_orderId, 100m, Key);
        var second = await business.ChargeAsync(_orderId, 100m, Key);

        Assert.False(second.Captured);
        Assert.Equal(1, authorizer.Calls);

        // The reason must be REPLAYABLE, which is why it is stored rather than recomputed. Recomputed
        // from a rule whose configuration had since changed, the replay could return a different
        // reason — or approve a charge that was previously declined.
        Assert.Equal(first.DeclineReason, second.DeclineReason);
        Assert.NotEmpty(second.DeclineReason);
    }

    [Fact]
    public async Task A_concurrent_duplicate_that_loses_the_insert_race_returns_the_WINNERS_outcome()
    {
        var (business, _) = Create();

        // Simulate the racer: another process inserted the row for this key first.
        await _data.TryInsertAsync(new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = _orderId,
            Amount = 100m,
            Status = PaymentStatus.Captured,
            AuthorizationCode = "AUTH-WINNER1",
            IdempotencyKey = Key,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });

        var result = await business.ChargeAsync(_orderId, 100m, Key);

        // We lost the race, so we report what the winner did — we do not charge again.
        Assert.Equal("AUTH-WINNER1", result.AuthorizationCode);
        Assert.Equal(1, _data.Count);
    }

    // ── Declines are outcomes, not exceptions ([R]3) ─────────────────────────────────────────────

    [Fact]
    public async Task A_charge_over_the_threshold_is_declined_rather_than_thrown()
    {
        var (business, _) = Create(new PaymentOptions { DeclineOverAmount = 1000m });

        var result = await business.ChargeAsync(_orderId, 1299.99m, Key);

        Assert.False(result.Captured);
        Assert.Contains("exceeds the authorization limit", result.DeclineReason);
    }

    [Fact]
    public async Task A_charge_with_NO_idempotency_key_throws_because_it_can_never_be_made_safe()
    {
        var (business, _) = Create();

        // A broken caller, not a broken card. Without a key there is nothing to collapse duplicates
        // onto, so every retry would be a fresh charge.
        await Assert.ThrowsAsync<ArgumentException>(() => business.ChargeAsync(_orderId, 100m, "   "));
    }

    [Fact]
    public async Task The_authorization_code_is_deterministic_so_a_crashed_charge_can_be_resolved_safely()
    {
        var (businessA, _) = Create();
        var (businessB, _) = Create();

        var a = await businessA.ChargeAsync(_orderId, 100m, Key);

        // A different process, resolving a Pending row it did not create, must reach the SAME answer.
        // A random auth code would make the row depend on how many times the message was redelivered.
        var b = await businessB.ChargeAsync(_orderId, 100m, Key);

        Assert.Equal(a.AuthorizationCode, b.AuthorizationCode);
    }

    // ── Refunds ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refund_flips_the_capture_and_keeps_the_auth_code_as_evidence()
    {
        var (business, _) = Create();

        await business.ChargeAsync(_orderId, 100m, Key);
        await business.RefundAsync(_orderId);

        var payment = (await _data.ListByOrderAsync(_orderId)).Single();

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(100m, payment.Amount);

        // [B]. The auth code is the only thing tying the refund back to the original capture — the
        // thing an auditor asks to see.
        Assert.NotEmpty(payment.AuthorizationCode);
    }

    [Fact]
    public async Task Refunding_an_order_with_nothing_captured_is_a_no_op()
    {
        var (business, _) = Create();

        // A compensation that throws when it has nothing to do dead-letters itself the second time it
        // is asked. This is the redelivered-refund case, and the declined-order case.
        await business.RefundAsync(Guid.NewGuid());

        Assert.Equal(0, _data.Count);
    }

    [Fact]
    public async Task Refunding_twice_does_not_refund_twice()
    {
        var (business, _) = Create();

        await business.ChargeAsync(_orderId, 100m, Key);
        await business.RefundAsync(_orderId);
        await business.RefundAsync(_orderId);   // the broker redelivers

        var payment = (await _data.ListByOrderAsync(_orderId)).Single();

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }
}
