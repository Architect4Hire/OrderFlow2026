import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';

import { OrderStatus, SkuAvailability, StuckOrder } from '../../core/models/models';
import { FulfillmentService } from '../../core/services/fulfillment.service';
import { InventoryService } from '../../core/services/inventory.service';
import { OrderService } from '../../core/services/order.service';

const POLL_INTERVAL_MS = 5_000;

/**
 * The ops surface. It answers the first question a real operations lead asks: **show me what is stuck,
 * and why.**
 *
 * Read-only, deliberately (G5 [R]1). There is no button here to release a hold, force a state, or retry a
 * dispatch — not because those are hard, but because an operator poking the saga's state from the outside
 * is exactly the thing a saga exists to prevent. Recovery is automatic (the sweeper re-drives stuck
 * orders); this screen is how you watch it work, and how you see the ones it cannot fix.
 */
@Component({
  selector: 'of-ops',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="row" style="justify-content: space-between; align-items: baseline;">
      <h2>Operations</h2>
      <span class="small muted">read-only · refreshing every {{ pollSeconds }}s</span>
    </div>

    @if (error(); as message) {
      <div class="banner banner-danger">{{ message }}</div>
    }

    <!-- ── Inventory ────────────────────────────────────────────────────────────────────────────── -->
    <div class="panel">
      <h3>Inventory</h3>
      <p class="small muted" style="margin-top: -0.4rem;">
        Watch the gap between on-hand and available: that is stock the saga is holding. If it never closes,
        a compensation was lost.
      </p>

      @if (skus().length === 0) {
        <p class="muted small">No stock loaded.</p>
      } @else {
        <table>
          <thead>
            <tr>
              <th>SKU</th>
              <th class="num">Unit price</th>
              <th class="num">On hand</th>
              <th class="num">Reserved</th>
              <th class="num">Available</th>
            </tr>
          </thead>
          <tbody>
            @for (sku of skus(); track sku.sku) {
              <tr>
                <td class="mono">{{ sku.sku }}</td>
                <td class="num muted">{{ money(sku.unitPrice) }}</td>
                <td class="num">{{ sku.onHand }}</td>
                <td class="num" [class.is-warn]="sku.reserved > 0">{{ sku.reserved }}</td>
                <!-- Nothing left to sell. The next order for this SKU is rejected outright. -->
                <td class="num" [class.is-danger]="sku.available <= 0">{{ sku.available }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    </div>

    <!-- ── Active orders ────────────────────────────────────────────────────────────────────────── -->
    <div class="panel">
      <h3>Active orders</h3>
      <p class="small muted" style="margin-top: -0.4rem;">
        Orders in flight — placed but not yet Confirmed or Failed. A healthy order crosses all five
        services in under a second, so anything lingering here is worth a second look.
      </p>

      @if (active().length === 0) {
        <p class="muted small">Nothing in flight.</p>
      } @else {
        <table>
          <thead>
            <tr>
              <th>Order</th>
              <th>Customer</th>
              <th>State</th>
              <th class="num">Total</th>
              <th>Updated</th>
            </tr>
          </thead>
          <tbody>
            @for (order of active(); track order.id) {
              <tr>
                <td class="mono small">{{ order.id }}</td>
                <td>{{ order.customerRef }}</td>
                <td><span class="badge" [class.is-warn]="order.state === 'Placed'">{{ order.state }}</span></td>
                <td class="num">{{ money(order.total) }}</td>
                <td class="small muted">{{ time(order.updatedUtc) }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    </div>

    <!-- ── Stuck / dead-lettered ────────────────────────────────────────────────────────────────── -->
    <div class="panel">
      <h3>Stuck / dead-lettered</h3>
      <p class="small muted" style="margin-top: -0.4rem;">
        Messages the broker gave up on. Each one is an order nobody was told about: the saga is still
        waiting and will wait forever without help. A cleanly-failed order is <em>not</em> here — it was
        answered and compensated.
      </p>

      @if (stuck().length === 0) {
        <p class="muted small">Nothing stuck. Every message was answered.</p>
      } @else {
        <table>
          <thead>
            <tr>
              <th>Source</th>
              <th>Order</th>
              <th>Reason</th>
              <th class="num">Deliveries</th>
              <th>Enqueued</th>
            </tr>
          </thead>
          <tbody>
            @for (row of stuck(); track row.messageId) {
              <tr class="row-warn">
                <td class="mono small">{{ row.source }}</td>
                <td class="mono small">{{ row.orderId }}</td>
                <!--
                  The reason IS the value of this panel (G5 [R]2). Hiding it behind a "stuck" badge would
                  turn the one screen that explains a failure into one that merely counts them.
                -->
                <td class="is-warn">
                  {{ row.reason || 'Unknown' }}
                  @if (row.errorDescription) {
                    <div class="small muted">{{ row.errorDescription }}</div>
                  }
                </td>
                <td class="num">{{ row.deliveryCount }}</td>
                <td class="small muted">{{ time(row.enqueuedUtc) }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    </div>
  `,
})
export class OpsComponent implements OnInit, OnDestroy {
  private readonly orders = inject(OrderService);
  private readonly inventory = inject(InventoryService);
  private readonly fulfillment = inject(FulfillmentService);

  protected readonly pollSeconds = POLL_INTERVAL_MS / 1_000;

  protected readonly skus = signal<SkuAvailability[]>([]);
  protected readonly active = signal<OrderStatus[]>([]);
  protected readonly stuck = signal<StuckOrder[]>([]);
  protected readonly error = signal('');

  private pollHandle: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.pollHandle = setInterval(() => this.refresh(), POLL_INTERVAL_MS);
  }

  ngOnDestroy(): void {
    if (this.pollHandle !== null) {
      clearInterval(this.pollHandle);
      this.pollHandle = null;
    }
  }

  private refresh(): void {
    // Each panel is fetched independently and failures are per-panel: if Fulfillment is down, the stock
    // and active-order panels must still render. A single combined subscription that errored would blank
    // the entire ops screen at precisely the moment an operator most needs to look at it.
    this.inventory.listSkus().subscribe({
      next: (skus) => this.skus.set(skus),
      error: () => this.error.set('The Inventory service is not answering.'),
    });

    this.orders.listActive().subscribe({
      next: (orders) => this.active.set(orders),
      error: () => this.error.set('The Orders service is not answering.'),
    });

    // Two sources, one panel. Fulfillment's endpoint reports failed dispatches; the Orders endpoint
    // reports every other dead-letter queue in the system — including release-inventory and
    // refund-payment, which are the rows where stock is stranded and a customer is out of pocket. Showing
    // only the first would leave the screen looking clean while money is stuck.
    forkJoin({
      dispatches: this.fulfillment.listStuck(),
      deadLetters: this.orders.listDeadLetters(),
    }).subscribe({
      next: ({ dispatches, deadLetters }) => {
        const merged = [...deadLetters, ...dispatches];

        // The Orders dead-letter feed already includes the dispatch-fulfillment queue, so the two sources
        // overlap. Dedupe on the broker's message id — the same message must not appear twice and imply
        // two stuck orders where there is one.
        const unique = new Map(merged.map((row) => [row.messageId, row]));

        this.stuck.set(
          [...unique.values()].sort((a, b) => b.enqueuedUtc.localeCompare(a.enqueuedUtc)),
        );
      },
      error: () => this.error.set('Could not read the dead-letter queues.'),
    });
  }

  protected money(value: number): string {
    return value.toFixed(2);
  }

  protected time(iso: string): string {
    const parsed = new Date(iso);

    return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleTimeString();
  }
}
