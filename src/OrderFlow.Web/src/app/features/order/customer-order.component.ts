import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';

import { ORDER_STEPS, OrderState, OrderStatus, SkuAvailability, isTerminal } from '../../core/models/models';
import { InventoryService } from '../../core/services/inventory.service';
import { OrderService } from '../../core/services/order.service';

interface LineDraft {
  sku: string;
  quantity: number;
}

/** How often the status view asks. Fast enough to watch the saga move, slow enough not to hammer Redis. */
const POLL_INTERVAL_MS = 2_000;

/**
 * The customer surface. Its whole job is to make the saga's asynchronous progression visible.
 *
 * The POST returns an order in state `Placed` — never a finished one. Inventory, payment and fulfillment
 * all happen afterwards, over the bus, and arrive here on subsequent polls. A UI written on the
 * assumption that placing an order *completes* it would show "done" the instant the 201 landed and then
 * be wrong for the next several hundred milliseconds — and catastrophically wrong on any failure path,
 * where the order ends up Failed and refunded while the screen still says success.
 */
@Component({
  selector: 'of-customer-order',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2>Place an order</h2>

    @if (error(); as message) {
      <div class="banner banner-danger">{{ message }}</div>
    }

    <div class="panel">
      <div class="row" style="margin-bottom: 1rem;">
        <label class="small muted" for="customerRef">Customer</label>
        <input
          id="customerRef"
          [value]="customerRef()"
          (input)="onCustomerRef($event)"
          [disabled]="locked()"
          style="width: 12rem;"
        />
      </div>

      <table>
        <thead>
          <tr>
            <th style="width: 45%;">SKU</th>
            <th style="width: 15%;">Qty</th>
            <th class="num">Unit price</th>
            <th class="num">Line</th>
            <th style="width: 3rem;"></th>
          </tr>
        </thead>
        <tbody>
          @for (line of lines(); track $index) {
            <tr>
              <td>
                <select [value]="line.sku" (change)="onSku($index, $event)" [disabled]="locked()" style="width: 100%;">
                  <option value="">— choose a SKU —</option>
                  @for (item of catalogue(); track item.sku) {
                    <option [value]="item.sku">{{ item.sku }} ({{ item.available }} available)</option>
                  }
                </select>
              </td>
              <td>
                <input
                  type="number"
                  min="1"
                  max="100"
                  [value]="line.quantity"
                  (input)="onQuantity($index, $event)"
                  [disabled]="locked()"
                  style="width: 100%;"
                />
              </td>
              <!--
                Displayed, never collected. The catalogue price is shown so the customer knows what they
                are agreeing to, but it is rendered FROM the server's answer and is not an input — there
                is no field here whose value could travel back. See PlaceOrderLine: the request carries a
                SKU and a quantity, and nothing else (ADR-006).
              -->
              <td class="num muted">{{ money(priceOf(line.sku)) }}</td>
              <td class="num">{{ money(priceOf(line.sku) * line.quantity) }}</td>
              <td>
                @if (lines().length > 1) {
                  <button class="ghost" (click)="removeLine($index)" [disabled]="locked()" title="Remove line">
                    ×
                  </button>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>

      <div class="row" style="justify-content: space-between; margin-top: 1rem;">
        <button class="ghost" (click)="addLine()" [disabled]="locked()">+ Add line</button>
        <div class="row">
          <span class="small muted">Catalogue total</span>
          <span class="mono" style="font-size: 1.05rem;">{{ money(estimate()) }}</span>
        </div>
      </div>

      <div class="row" style="margin-top: 1rem;">
        <button (click)="submit()" [disabled]="!canSubmit()">
          {{ submitting() ? 'Placing…' : 'Place order' }}
        </button>
        @if (orderId()) {
          <button class="ghost" (click)="reset()">Place another</button>
        }
        <span class="small muted">
          The server prices the order. This total is the catalogue's, shown for information.
        </span>
      </div>
    </div>

    @if (status(); as order) {
      <h2>Progress</h2>
      <div class="panel">
        <div class="row small muted" style="margin-bottom: 1rem; gap: 1.25rem;">
          <span>Order <code>{{ order.id }}</code></span>
          @if (polling()) {
            <span class="is-warn">● polling every {{ pollSeconds }}s</span>
          } @else {
            <span>■ settled — polling stopped</span>
          }
        </div>

        @if (order.state === 'Failed') {
          <!--
            The failure path is the one worth showing. The order was compensated: whatever had been
            reserved is released, and anything charged is refunded. Saying "failed" without the reason
            would waste the single most useful fact the saga produced.
          -->
          <div class="banner banner-danger" style="margin-bottom: 1rem;">
            <strong>Failed.</strong>
            {{ order.failureReason || 'No reason was recorded.' }}
            <div class="small" style="margin-top: 0.35rem; opacity: 0.85;">
              Any stock held for this order has been released and any payment taken has been refunded.
            </div>
          </div>
        }

        <div class="stepper">
          @for (step of steps; track step; let i = $index) {
            <div
              class="step"
              [class.done]="reached(i)"
              [class.current]="isCurrent(step)"
              [class.dead]="order.state === 'Failed'"
            >
              <span class="dot"></span>
              <span class="label">{{ step }}</span>
            </div>
          }
        </div>

        @if (order.lines.length > 0 && order.total > 0) {
          <table style="margin-top: 1.25rem;">
            <thead>
              <tr>
                <th>SKU</th>
                <th class="num">Qty</th>
                <th class="num">Unit price</th>
                <th class="num">Line total</th>
              </tr>
            </thead>
            <tbody>
              @for (line of order.lines; track line.id) {
                <tr>
                  <td class="mono">{{ line.sku }}</td>
                  <td class="num">{{ line.quantity }}</td>
                  <td class="num">{{ money(line.unitPrice) }}</td>
                  <td class="num">{{ money(line.lineTotal) }}</td>
                </tr>
              }
              <tr>
                <td colspan="3" class="num muted">Charged</td>
                <td class="num"><strong>{{ money(order.total) }}</strong></td>
              </tr>
            </tbody>
          </table>
        }
      </div>
    }
  `,
  styles: `
    .stepper {
      display: flex;
      align-items: flex-start;
    }

    .step {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 0.4rem;
      position: relative;
      color: var(--text-muted);
      font-size: 0.8rem;
    }

    /* The connecting rail. Drawn behind the dot, from each step back to the previous one. */
    .step:not(:first-child)::before {
      content: '';
      position: absolute;
      top: 6px;
      right: 50%;
      left: -50%;
      height: 2px;
      background: var(--border);
    }

    .step.done:not(:first-child)::before {
      background: var(--accent);
    }

    .dot {
      width: 14px;
      height: 14px;
      border-radius: 50%;
      background: var(--bg-deep);
      border: 2px solid var(--border);
      z-index: 1;
    }

    .step.done .dot {
      background: var(--accent);
      border-color: var(--accent);
    }

    .step.current .dot {
      box-shadow: 0 0 0 4px rgba(194, 65, 12, 0.25);
    }

    .step.current .label {
      color: var(--text-primary);
      font-weight: 600;
    }

    .step.dead .dot {
      border-color: var(--danger);
      background: var(--bg-deep);
    }

    .step.dead.done .dot {
      background: var(--danger);
      border-color: var(--danger);
    }

    .step.dead:not(:first-child)::before {
      background: var(--border);
    }
  `,
})
export class CustomerOrderComponent implements OnInit, OnDestroy {
  private readonly orders = inject(OrderService);
  private readonly inventory = inject(InventoryService);

  protected readonly steps = ORDER_STEPS;
  protected readonly pollSeconds = POLL_INTERVAL_MS / 1_000;

  protected readonly catalogue = signal<SkuAvailability[]>([]);
  protected readonly customerRef = signal('CUST-001');
  protected readonly lines = signal<LineDraft[]>([{ sku: '', quantity: 1 }]);

  protected readonly orderId = signal<string | null>(null);
  protected readonly status = signal<OrderStatus | null>(null);
  protected readonly submitting = signal(false);
  protected readonly polling = signal(false);
  protected readonly error = signal('');

  /** The catalogue's arithmetic, for information only. The server's number is the one that counts. */
  protected readonly estimate = computed(() =>
    this.lines().reduce((total, line) => total + this.priceOf(line.sku) * line.quantity, 0),
  );

  /** Once an order is away, the form is a record of what was sent — editing it would be a lie. */
  protected readonly locked = computed(() => this.submitting() || this.orderId() !== null);

  protected readonly canSubmit = computed(
    () =>
      !this.locked() &&
      this.customerRef().trim().length > 0 &&
      this.lines().length > 0 &&
      this.lines().every((line) => line.sku.length > 0 && line.quantity > 0),
  );

  private pollHandle: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.inventory.listSkus().subscribe({
      next: (skus) => this.catalogue.set(skus),
      error: () => this.error.set('Could not load the catalogue from the Inventory service.'),
    });
  }

  /**
   * Belt and braces with the terminal-state stop below. A component can be destroyed long before its
   * order settles — navigate to Ops mid-saga and this timer would otherwise poll a dead component
   * forever, and every one of those requests would still hit the API.
   */
  ngOnDestroy(): void {
    this.stopPolling();
  }

  protected submit(): void {
    if (!this.canSubmit()) {
      return;
    }

    this.submitting.set(true);
    this.error.set('');

    // Note what is sent: customerRef, and per line a sku and a quantity. No price, no state, no id.
    this.orders
      .place({
        customerRef: this.customerRef().trim(),
        lines: this.lines().map((line) => ({ sku: line.sku, quantity: line.quantity })),
      })
      .subscribe({
        next: (order) => {
          this.submitting.set(false);
          this.orderId.set(order.id);
          this.status.set(order);
          this.startPolling(order.id);
        },
        error: () => {
          this.submitting.set(false);
          this.error.set('The order could not be placed. The Orders service rejected or did not answer the request.');
        },
      });
  }

  protected reset(): void {
    this.stopPolling();
    this.orderId.set(null);
    this.status.set(null);
    this.error.set('');
    this.lines.set([{ sku: '', quantity: 1 }]);
  }

  protected addLine(): void {
    this.lines.update((lines) => [...lines, { sku: '', quantity: 1 }]);
  }

  protected removeLine(index: number): void {
    this.lines.update((lines) => lines.filter((_, i) => i !== index));
  }

  protected onCustomerRef(event: Event): void {
    this.customerRef.set((event.target as HTMLInputElement).value);
  }

  protected onSku(index: number, event: Event): void {
    const sku = (event.target as HTMLSelectElement).value;
    this.lines.update((lines) => lines.map((line, i) => (i === index ? { ...line, sku } : line)));
  }

  protected onQuantity(index: number, event: Event): void {
    const parsed = Number.parseInt((event.target as HTMLInputElement).value, 10);
    const quantity = Number.isNaN(parsed) ? 1 : Math.min(Math.max(parsed, 1), 100);
    this.lines.update((lines) => lines.map((line, i) => (i === index ? { ...line, quantity } : line)));
  }

  protected priceOf(sku: string): number {
    return this.catalogue().find((item) => item.sku === sku)?.unitPrice ?? 0;
  }

  /** Two decimals, no currency symbol — the services never say which currency this is, so neither do we. */
  protected money(value: number): string {
    return value.toFixed(2);
  }

  protected isCurrent(step: OrderState): boolean {
    return this.status()?.state === step;
  }

  /** A step is lit once the order has reached it or passed it. `Failed` is off the happy path entirely. */
  protected reached(index: number): boolean {
    const state = this.status()?.state;

    if (state === undefined || state === 'Failed') {
      return false;
    }

    return index <= ORDER_STEPS.indexOf(state);
  }

  private startPolling(id: string): void {
    this.stopPolling();
    this.polling.set(true);
    this.pollHandle = setInterval(() => this.refresh(id), POLL_INTERVAL_MS);
  }

  private refresh(id: string): void {
    this.orders.getStatus(id).subscribe({
      next: (order) => {
        this.status.set(order);

        // Confirmed and Failed are the end of the story. Polling past them burns requests forever and
        // clutters the trace the demo is trying to show.
        if (isTerminal(order.state)) {
          this.stopPolling();
        }
      },
      // A failed poll is not a failed order — the status service may just have blinked. Keep polling;
      // that is the whole point of a retry loop. If it is genuinely down, the next tick says so too.
      error: () => this.error.set('Lost contact with the Orders service while polling. Still trying.'),
    });
  }

  private stopPolling(): void {
    if (this.pollHandle !== null) {
      clearInterval(this.pollHandle);
      this.pollHandle = null;
    }

    this.polling.set(false);
  }
}
