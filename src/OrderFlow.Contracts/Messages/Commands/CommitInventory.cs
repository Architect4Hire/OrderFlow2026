namespace OrderFlow.Contracts.Messages;

/// <summary>
/// Saga → Inventory: the goods shipped. Turn this order's holds into a permanent stock decrement.
/// No reply event — fire-and-forget, like the compensations.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the happy path never closes. A hold taken at reservation would stay <c>Held</c>
/// forever and <c>OnHand</c> would never fall, so the warehouse row would permanently overstate what
/// is physically on the shelf.
/// </para>
/// <para>
/// The arithmetic would survive that — <c>Available = OnHand - Reserved</c> is unchanged whether a
/// hold is permanent or the stock is decremented — but the DIAGNOSTIC would not. The ops view would
/// fill with <c>Held</c> rows for orders that shipped weeks ago, sitting alongside the <c>Held</c>
/// rows stranded by a lost compensation, and the two would be indistinguishable. Being able to tell
/// those apart is the entire point of this system.
/// </para>
/// <para>
/// Committing an order with no live holds is a valid no-op, so a redelivery is safe.
/// </para>
/// </remarks>
public record CommitInventory : MessageBase;
