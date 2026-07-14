using OrderFlow.Contracts.Messages;
using OrderFlow.Fulfillment.API.Managers.Data;
using OrderFlow.Fulfillment.API.Managers.Extensions;
using OrderFlow.Fulfillment.API.Managers.ServiceModels;

namespace OrderFlow.Fulfillment.API.Managers.Business;

/// <summary>
/// Dispatched, or hard-failed. There is no third answer — and note what is NOT here: "try again
/// later". That case never reaches this type, because it is not a business outcome (see the
/// remarks on <see cref="FulfillmentBusinessManager"/>).
/// </summary>
public sealed record DispatchResult(bool Dispatched, string TrackingRef, string FailureReason)
{
    public static DispatchResult Success(string trackingRef) => new(true, trackingRef, string.Empty);

    public static DispatchResult HardFailure(string reason) => new(false, string.Empty, reason);
}

public interface IFulfillmentBusinessManager
{
    Task<DispatchResult> DispatchAsync(
        Guid orderId,
        string customerRef,
        IReadOnlyList<OrderLine> lines,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StuckDispatchServiceModel>> ListStuckAsync(
        int maxMessages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls the carrier and turns the result into an answer the saga can act on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The distinction this class exists to draw: a FINAL failure is not the same as a FAILURE TO
/// ASK.</b>
/// </para>
/// <para>
/// A <see cref="CarrierException"/> — retries exhausted, or a permanent rejection — is the carrier
/// giving us a final answer. It is a business outcome. We catch it, return a hard failure, and the
/// consumer publishes FulfillmentFailed so the saga refunds the payment and releases the stock. The
/// order dies cleanly and the customer gets their money back.
/// </para>
/// <para>
/// A <c>BrokenCircuitException</c> is NOT caught, deliberately. The breaker being open means we
/// never spoke to the carrier at all — we have no answer, only an absence of one. Turning that into
/// FulfillmentFailed would kill a perfectly good order, refund a customer who was about to get their
/// parcel, and release stock that did not need releasing, all because a dependency was briefly
/// unhealthy. Letting it propagate makes the consumer abandon the message so the broker redelivers it
/// once the breaker has closed. If it never closes, the message eventually dead-letters and lands in
/// the ops view — visible, diagnosable, replayable. That is the correct end state for "we could not
/// find out", and it is a different end state from "the answer was no".
/// </para>
/// </remarks>
public class FulfillmentBusinessManager(
    ICarrierClient carrierClient,
    IDeadLetterData deadLetterData,
    ILogger<FulfillmentBusinessManager> logger) : IFulfillmentBusinessManager
{
    public async Task<DispatchResult> DispatchAsync(
        Guid orderId,
        string customerRef,
        IReadOnlyList<OrderLine> lines,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var trackingRef = await carrierClient.DispatchAsync(orderId, customerRef, lines, cancellationToken);

            return DispatchResult.Success(trackingRef);
        }
        catch (CarrierException ex)
        {
            // [R]3 — not swallowed. It is converted into an outcome the saga can compensate on, and
            // logged at Error because a hard dispatch failure means a customer's paid order is about
            // to be unwound.
            logger.LogError(
                ex, "Dispatch for order {OrderId} failed hard. The saga will refund and release.", orderId);

            return DispatchResult.HardFailure(ex.Message);
        }

        // BrokenCircuitException is intentionally NOT handled here. See the class remarks: it means
        // we never asked, and an order must not be killed because we could not find out.
    }

    public async Task<IReadOnlyList<StuckDispatchServiceModel>> ListStuckAsync(
        int maxMessages,
        CancellationToken cancellationToken = default) =>
        (await deadLetterData.PeekDeadLetteredAsync(maxMessages, cancellationToken)).ToServiceModels();
}

public static class FulfillmentBusinessExtensions
{
    public static IHostApplicationBuilder AddFulfillmentBusiness(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IFulfillmentBusinessManager, FulfillmentBusinessManager>();

        return builder;
    }
}
