/**
 * The wire contracts, mirroring the services' ServiceModels. ASP.NET Core serializes camelCase by
 * default, so these names match the JSON as sent.
 *
 * These are the *read* shapes plus the single *write* shape the client is allowed to assert. Nothing
 * here carries state, identity, or money that the client gets to choose — see {@link PlaceOrderLine}.
 */

/** The saga's states, as names. The API sends `State` as a string precisely so a renumbering of the
 *  C# enum cannot silently change what the client sees. */
export type OrderState = 'Placed' | 'Reserved' | 'Paid' | 'Dispatched' | 'Confirmed' | 'Failed';

/** The happy path, in order. `Failed` is not here — it is a terminal branch, not a step. */
export const ORDER_STEPS: readonly OrderState[] = ['Placed', 'Reserved', 'Paid', 'Dispatched', 'Confirmed'];

export const isTerminal = (state: OrderState): boolean => state === 'Confirmed' || state === 'Failed';

// ── write ────────────────────────────────────────────────────────────────────────────────────────

/** What the customer posts. Mirrors PlaceOrderViewModel — nothing more. */
export interface PlaceOrder {
  customerRef: string;
  lines: PlaceOrderLine[];
}

/**
 * A requested line: a SKU and a quantity.
 *
 * **There is no unitPrice, and its absence is the security control** (ADR-006). It used to exist, and it
 * meant the browser set the price it would be charged — a customer could buy a laptop for a penny by
 * editing one field of the JSON they were already sending, and every service downstream would have
 * faithfully carried the number it was given. Prices now come from Inventory, which owns the catalogue.
 * The server rejects the field because the model it binds to does not have it. Do not add it back here
 * to "match the form": the form is what must not collect it.
 */
export interface PlaceOrderLine {
  sku: string;
  quantity: number;
}

// ── read ─────────────────────────────────────────────────────────────────────────────────────────

/** What the status view polls. Mirrors OrderServiceModel. */
export interface OrderStatus {
  id: string;
  customerRef: string;
  state: OrderState;
  subtotal: number;
  total: number;
  /** Set only on the failure paths; empty on a healthy order. */
  failureReason: string;
  lines: OrderLine[];
  createdUtc: string;
  updatedUtc: string;
}

/** A priced line as it comes back. `unitPrice` is present on the way OUT — the server decided it. */
export interface OrderLine {
  id: string;
  sku: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
}

/** Mirrors StockItemServiceModel. The gap between onHand and available is stock the saga is holding. */
export interface SkuAvailability {
  sku: string;
  onHand: number;
  /** The catalogue price — the number the customer actually gets charged (ADR-006). */
  unitPrice: number;
  reserved: number;
  /** onHand − reserved, computed server-side. The client never decides what "available" means. */
  available: number;
  updatedUtc: string;
}

/**
 * Mirrors PaymentServiceModel. A healthy order has exactly ONE of these — the saga keys every charge for
 * an order on the same idempotency key, so a second row means the guard failed and someone was charged
 * twice. The auth code arrives already masked; the full value is never sent to a browser.
 */
export interface PaymentAttempt {
  id: string;
  orderId: string;
  amount: number;
  status: string;
  isAuthorized: boolean;
  declineReason: string;
  authorizationCodeMasked: string;
  createdUtc: string;
  updatedUtc: string;
}

/**
 * A message the broker gave up on. Mirrors both StuckDispatchServiceModel (Fulfillment) and
 * DeadLetterServiceModel (Orders) — the two differ only in that Orders names the queue it came from.
 *
 * Every row is a failure nobody was told about: the saga is still waiting and will wait forever. A
 * cleanly-failed order does NOT appear here, because it was answered and compensated.
 */
export interface StuckOrder {
  /** Which queue or subscription. Empty from the Fulfillment endpoint, which only reports its own. */
  source: string;
  orderId: string;
  messageId: string;
  messageType: string;
  /** The broker's reason: MaxDeliveryCountExceeded, DeserializationFailed… */
  reason: string;
  errorDescription: string;
  deliveryCount: number;
  enqueuedUtc: string;
}
