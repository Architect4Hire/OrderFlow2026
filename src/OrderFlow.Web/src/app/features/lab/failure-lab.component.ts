import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';

import { OrderState, OrderStatus, PlaceOrder, SkuAvailability, isTerminal } from '../../core/models/models';
import { InventoryService } from '../../core/services/inventory.service';
import { OrderService } from '../../core/services/order.service';

/**
 * The failure lab. Its whole job is to make the failure matrix — the thing this system exists to prove —
 * something you can *drive from the browser* rather than something you have to read the README to believe.
 *
 * Two kinds of scenario live here, and the difference is not cosmetic:
 *
 *   • **Trigger now** — reachable purely by the *content* of an order, so a button is enough. The
 *     amount-decline path fires on any basket over the limit; the oversell race fires when two orders
 *     chase the last unit. No restart, no lever.
 *
 *   • **Set a lever, then trigger** — the four global failure switches (`payment-decline-all`,
 *     `carrier-failure-mode`, `notification-provider-*`) are bound once, at AppHost startup, through
 *     `IOptions<T>`. Nothing in a browser can flip them mid-run. So the honest thing to build is not a
 *     fake toggle: it is the exact command to set the lever, plus a Run button that places the order the
 *     scenario needs and then *tells you whether the lever actually took* — if the order came back
 *     Confirmed when the scenario expected a failure, the verdict says so and points back at the lever.
 *
 * This is deliberately NOT the ops view. Ops is read-only on principle — an operator poking the saga from
 * outside is the thing a saga exists to prevent. This screen injects *failures*, not *state*, and it is a
 * demo/diagnostic surface, kept separate for exactly that reason.
 */
@Component({
  selector: 'of-failure-lab',
  standalone: true,
  imports: [NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="row" style="justify-content: space-between; align-items: baseline;">
      <h2>Failure lab</h2>
      <span class="small muted">drive every row of the failure matrix</span>
    </div>

    @if (error(); as message) {
      <div class="banner banner-danger">{{ message }}</div>
    }

    <p class="small muted" style="max-width: 60ch;">
      Each card is one scenario. The saga's answer to a failure is the point — every card names what has
      already happened when the failure lands, and what the saga does to undo it. Run one and watch the
      order settle below the button.
    </p>

    <!-- ── Trigger now ──────────────────────────────────────────────────────────────────────────── -->
    <h3 style="margin-top: 1.5rem;">Trigger now</h3>
    <p class="small muted" style="margin-top: -0.4rem;">
      Reachable from the browser alone — no lever, no restart. The order's shape is enough.
    </p>

    @for (scenario of runtimeScenarios; track scenario.id) {
      <ng-container [ngTemplateOutlet]="card" [ngTemplateOutletContext]="{ $implicit: scenario }" />
    }

    <!-- ── Set a lever, then trigger ────────────────────────────────────────────────────────────── -->
    <h3 style="margin-top: 1.75rem;">Set a lever, then trigger</h3>
    <p class="small muted" style="margin-top: -0.4rem;">
      These four switches are bound at AppHost startup, so they cannot be flipped from here. Set the lever,
      restart, then Run — the verdict will confirm whether it took.
    </p>

    @for (scenario of leverScenarios; track scenario.id) {
      <ng-container [ngTemplateOutlet]="card" [ngTemplateOutletContext]="{ $implicit: scenario }" />
    }

    <!-- ── One card, used for both groups ───────────────────────────────────────────────────────── -->
    <ng-template #card let-scenario>
      <div class="panel">
        <div class="row" style="justify-content: space-between; align-items: baseline;">
          <h4 style="margin: 0;">{{ scenario.title }}</h4>
          <span class="badge" [class.is-warn]="scenario.group === 'lever'">
            {{ scenario.group === 'lever' ? 'needs a lever' : 'ready' }}
          </span>
        </div>

        <p class="small" style="margin: 0.5rem 0;">{{ scenario.proves }}</p>

        <div class="matrix small muted">
          <span><strong>Already happened:</strong> {{ scenario.alreadyHappened }}</span>
          <span><strong>Saga does:</strong> {{ scenario.sagaDoes }}</span>
          <span><strong>Expect:</strong> {{ scenario.expected }}</span>
        </div>

        @if (scenario.lever; as lever) {
          <div class="lever">
            <code>{{ lever.command }}</code>
            <button class="ghost small" (click)="copy(lever.command)" title="Copy command">copy</button>
          </div>
        }

        <div class="row" style="margin-top: 0.75rem;">
          <button (click)="run(scenario)" [disabled]="!canRun()">
            {{ activeId() === scenario.id ? 'Running…' : 'Run' }}
          </button>
          @if (activeId() === scenario.id) {
            <span class="small is-warn">● placing and polling to a terminal state…</span>
          }
        </div>

        <!-- Live, while this scenario is the one running. -->
        @if (activeId() === scenario.id && runOrders().length > 0) {
          <div class="outcome">
            @for (order of runOrders(); track order.id) {
              <span class="order-line">
                <code class="mono small">{{ short(order.id) }}</code>
                <span class="badge" [class.is-warn]="order.state === 'Placed'">{{ order.state }}</span>
              </span>
            }
          </div>
        }

        <!-- Settled result of the last run. -->
        @if (results()[scenario.id]; as result) {
          <div class="outcome">
            <div
              class="banner"
              [class.banner-danger]="result.verdict === 'mismatch'"
              [class.banner-ok]="result.verdict === 'match'"
              [class.banner-muted]="result.verdict === 'inconclusive'"
            >
              <strong>{{ verdictLabel(result.verdict) }}</strong>
              {{ result.note }}
            </div>

            @for (order of result.orders; track order.id) {
              <div class="order-line">
                <code class="mono small">{{ short(order.id) }}</code>
                <span
                  class="badge"
                  [class.is-danger]="order.state === 'Failed'"
                >{{ order.state }}</span>
                @if (order.failureReason) {
                  <span class="small muted">{{ order.failureReason }}</span>
                }
              </div>
            }
          </div>
        }
      </div>
    </ng-template>
  `,
  styles: `
    .matrix {
      display: flex;
      flex-direction: column;
      gap: 0.2rem;
      margin: 0.5rem 0;
    }

    .lever {
      display: flex;
      align-items: center;
      gap: 0.6rem;
      margin-top: 0.6rem;
    }

    .lever code {
      flex: 1;
      overflow-x: auto;
      white-space: nowrap;
      padding: 0.35rem 0.5rem;
    }

    .outcome {
      margin-top: 0.75rem;
      display: flex;
      flex-direction: column;
      gap: 0.4rem;
    }

    .order-line {
      display: flex;
      align-items: center;
      gap: 0.6rem;
    }

    .banner-ok {
      color: var(--accent);
      background: rgba(194, 65, 12, 0.1);
    }

    .banner-muted {
      color: var(--text-muted);
      background: rgba(161, 161, 170, 0.08);
    }
  `,
})
export class FailureLabComponent implements OnInit, OnDestroy {
  private readonly orders = inject(OrderService);
  private readonly inventory = inject(InventoryService);

  protected readonly runtimeScenarios = RUNTIME_SCENARIOS;
  protected readonly leverScenarios = LEVER_SCENARIOS;

  protected readonly catalogue = signal<SkuAvailability[]>([]);
  protected readonly error = signal('');

  /** The scenario currently running, or null. Only one runs at a time — one timer, one clear story. */
  protected readonly activeId = signal<string | null>(null);
  /** Live snapshots of the order(s) the active run placed. */
  protected readonly runOrders = signal<OrderStatus[]>([]);
  /** The settled outcome of the last run of each scenario, keyed by scenario id. */
  protected readonly results = signal<Record<string, RunResult>>({});

  /** A run needs a catalogue to build an order from, and no other run in flight. */
  protected readonly canRun = computed(() => this.catalogue().length > 0 && this.activeId() === null);

  private handle: ReturnType<typeof setInterval> | null = null;
  private runStartMs = 0;

  ngOnInit(): void {
    this.inventory.listSkus().subscribe({
      next: (skus) => this.catalogue.set(skus),
      error: () => this.error.set('Could not load the catalogue from the Inventory service. Runs are disabled until it answers.'),
    });
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  protected run(scenario: Scenario): void {
    if (!this.canRun()) {
      return;
    }

    const plan = scenario.build(this.catalogue());

    // The catalogue could not supply an order that exercises this scenario — most often the
    // amount-decline path when nothing in stock adds up to the limit. Say so rather than place a
    // silently harmless order that would then "pass" for the wrong reason.
    if (plan === null) {
      this.setResult(scenario.id, {
        verdict: 'inconclusive',
        orders: [],
        note: scenario.unavailable ?? 'The current catalogue cannot build an order that triggers this scenario.',
      });
      return;
    }

    this.clearResult(scenario.id);
    this.activeId.set(scenario.id);
    this.runOrders.set([]);

    forkJoin(plan.map((order) => this.orders.place(order))).subscribe({
      next: (placed) => {
        this.runOrders.set(placed);
        this.runStartMs = Date.now();
        this.startPolling(scenario);
      },
      error: () => {
        this.activeId.set(null);
        this.setResult(scenario.id, {
          verdict: 'inconclusive',
          orders: [],
          note: 'The order could not be placed — the Orders service rejected or did not answer the request.',
        });
      },
    });
  }

  protected copy(text: string): void {
    void navigator.clipboard?.writeText(text);
  }

  protected short(id: string): string {
    return id.slice(0, 8);
  }

  protected verdictLabel(verdict: Verdict): string {
    switch (verdict) {
      case 'match':
        return 'As expected.';
      case 'mismatch':
        return 'Not what the scenario expected.';
      case 'inconclusive':
        return 'Inconclusive.';
    }
  }

  private startPolling(scenario: Scenario): void {
    this.stopPolling();
    this.handle = setInterval(() => this.tick(scenario), POLL_INTERVAL_MS);
  }

  private tick(scenario: Scenario): void {
    const current = this.runOrders();
    const pending = current.filter((order) => !isTerminal(order.state));

    // Everything settled, or we have waited long enough that a message is almost certainly stuck. Either
    // way, stop polling and let assess() read the final states.
    if (pending.length === 0 || Date.now() - this.runStartMs > RUN_TIMEOUT_MS) {
      this.finalize(scenario);
      return;
    }

    forkJoin(pending.map((order) => this.orders.getStatus(order.id))).subscribe({
      next: (updated) => {
        const byId = new Map(updated.map((order) => [order.id, order]));
        this.runOrders.set(current.map((order) => byId.get(order.id) ?? order));

        if (this.runOrders().every((order) => isTerminal(order.state))) {
          this.finalize(scenario);
        }
      },
      // A blinked poll is not a failed run — the next tick tries again, and the timeout is the backstop.
      error: () => undefined,
    });
  }

  private finalize(scenario: Scenario): void {
    this.stopPolling();

    const orders = this.runOrders();
    const settled = orders.length > 0 && orders.every((order) => isTerminal(order.state));

    const result: RunResult = settled
      ? { orders, ...scenario.assess(orders) }
      : {
          verdict: 'inconclusive',
          orders,
          note: 'The order never reached a terminal state within the timeout. Check Ops — there may be a stuck message the sweeper has not yet re-driven.',
        };

    this.setResult(scenario.id, result);
    this.activeId.set(null);
    this.runOrders.set([]);
  }

  private stopPolling(): void {
    if (this.handle !== null) {
      clearInterval(this.handle);
      this.handle = null;
    }
  }

  private setResult(id: string, result: RunResult): void {
    this.results.update((all) => ({ ...all, [id]: result }));
  }

  private clearResult(id: string): void {
    this.results.update((all) => {
      const { [id]: _removed, ...rest } = all;

      return rest;
    });
  }
}

// ── polling / timing ─────────────────────────────────────────────────────────────────────────────

/** How often a run re-reads each order's state. Fast enough to watch the saga move. */
const POLL_INTERVAL_MS = 2_000;

/**
 * How long a run waits before calling an order stuck. The whole matrix settles in well under a second on
 * a healthy system; the transient-carrier paths add a few retries with backoff. A generous ceiling keeps
 * a genuinely stranded order from being mislabelled while it is still legitimately in flight.
 */
const RUN_TIMEOUT_MS = 60_000;

/** The customer reference every lab order carries, so they are easy to spot in Ops and the traces. */
const LAB_CUSTOMER = 'LAB-001';

/** The default `payment-decline-over-amount` limit. The lab cannot read the live value, so it assumes it. */
const DEFAULT_DECLINE_LIMIT = 1_000;

// ── scenario model ───────────────────────────────────────────────────────────────────────────────

type Verdict = 'match' | 'mismatch' | 'inconclusive';

interface RunResult {
  verdict: Verdict;
  orders: OrderStatus[];
  note: string;
}

interface Scenario {
  id: string;
  title: string;
  group: 'runtime' | 'lever';
  /** One line on what this scenario proves. */
  proves: string;
  /** The failure-matrix column: what has already happened when the failure lands. */
  alreadyHappened: string;
  /** The failure-matrix column: what the saga does about it. */
  sagaDoes: string;
  /** The terminal outcome a correct run should reach. */
  expected: string;
  /** For lever scenarios: the exact command that sets the switch. */
  lever?: { command: string };
  /** Shown when {@link build} returns null. */
  unavailable?: string;
  /** Build the order(s) that exercise this scenario, or null if the catalogue cannot. */
  build(catalogue: SkuAvailability[]): PlaceOrder[] | null;
  /** Read the settled order(s) and decide whether the scenario played out as expected. */
  assess(orders: OrderStatus[]): { verdict: Verdict; note: string };
}

// ── catalogue helpers ────────────────────────────────────────────────────────────────────────────

const inStock = (catalogue: SkuAvailability[]): SkuAvailability[] =>
  catalogue.filter((item) => item.available > 0 && item.unitPrice > 0);

/** One line for the cheapest available SKU. Deliberately cheap so it clears the amount limit and only the
 *  scenario under test can make it fail. */
function cheapestSingleLine(catalogue: SkuAvailability[]): PlaceOrder[] | null {
  const cheapest = [...inStock(catalogue)].sort((a, b) => a.unitPrice - b.unitPrice)[0];

  return cheapest ? [{ customerRef: LAB_CUSTOMER, lines: [{ sku: cheapest.sku, quantity: 1 }] }] : null;
}

/** A basket whose total exceeds a target, taking the minimum quantity of the priciest SKUs first. */
function basketExceeding(catalogue: SkuAvailability[], target: number): PlaceOrder[] | null {
  const byPrice = [...inStock(catalogue)].sort((a, b) => b.unitPrice - a.unitPrice);
  const lines: { sku: string; quantity: number }[] = [];
  let total = 0;

  for (const item of byPrice) {
    if (total > target) {
      break;
    }

    const needed = Math.floor((target - total) / item.unitPrice) + 1;
    const quantity = Math.min(item.available, 100, needed);

    if (quantity <= 0) {
      continue;
    }

    lines.push({ sku: item.sku, quantity });
    total += quantity * item.unitPrice;
  }

  return total > target ? [{ customerRef: LAB_CUSTOMER, lines }] : null;
}

/** Two orders that both claim the entire available stock of the nearest-to-sold-out SKU. Combined they
 *  demand twice what exists, so exactly one can win the row-version race. */
function oversellPair(catalogue: SkuAvailability[]): PlaceOrder[] | null {
  const scarcest = [...inStock(catalogue)].sort((a, b) => a.available - b.available)[0];

  if (!scarcest) {
    return null;
  }

  const line = { sku: scarcest.sku, quantity: scarcest.available };

  return [
    { customerRef: `${LAB_CUSTOMER}-A`, lines: [line] },
    { customerRef: `${LAB_CUSTOMER}-B`, lines: [line] },
  ];
}

// ── assess helpers ───────────────────────────────────────────────────────────────────────────────

const allInState = (orders: OrderStatus[], state: OrderState): boolean =>
  orders.every((order) => order.state === state);

/** A lever scenario that expects a clean failure. If the order confirmed, the lever is not set. */
function expectFailure(orders: OrderStatus[], leverHint: string): { verdict: Verdict; note: string } {
  const order = orders[0];

  if (order.state === 'Failed') {
    return { verdict: 'match', note: order.failureReason || 'Failed and compensated as expected.' };
  }

  return {
    verdict: 'mismatch',
    note: `The order ended ${order.state}, not Failed — ${leverHint} Set the lever above, restart the AppHost, and run this again.`,
  };
}

/** A lever scenario whose whole point is that the order still completes despite the fault. */
function expectStillConfirms(orders: OrderStatus[], note: string): { verdict: Verdict; note: string } {
  if (allInState(orders, 'Confirmed')) {
    return { verdict: 'match', note };
  }

  return {
    verdict: 'mismatch',
    note: `The order ended ${orders[0].state}. This path should still confirm — the fault is absorbed, not fatal.`,
  };
}

// ── the scenarios ────────────────────────────────────────────────────────────────────────────────

const RUNTIME_SCENARIOS: readonly Scenario[] = [
  {
    id: 'happy-path',
    title: 'Happy path',
    group: 'runtime',
    proves: 'The baseline: reserve, charge, ship, notify — all five services agree.',
    alreadyHappened: 'Nothing has failed.',
    sagaDoes: 'Drives the order to Confirmed. No compensation.',
    expected: 'Confirmed.',
    build: cheapestSingleLine,
    unavailable: 'No SKU has stock to buy.',
    assess: (orders) =>
      allInState(orders, 'Confirmed')
        ? { verdict: 'match', note: 'Placed → Reserved → Paid → Dispatched → Confirmed.' }
        : {
            verdict: 'mismatch',
            note: `The order ended ${orders[0].state}. A failure lever may be set — check payment-decline-all and carrier-failure-mode.`,
          },
  },
  {
    id: 'payment-declined-amount',
    title: 'Payment declined — over the limit',
    group: 'runtime',
    proves: 'The decline path, triggered by order value alone. The inventory hold comes back.',
    alreadyHappened: 'Stock is held; the charge is refused.',
    sagaDoes: 'ReleaseInventory, then fails the order.',
    expected: 'Failed, with the decline reason.',
    build: (catalogue) => basketExceeding(catalogue, DEFAULT_DECLINE_LIMIT),
    unavailable: `Nothing in stock adds up to more than ${DEFAULT_DECLINE_LIMIT}, so the amount limit cannot be tripped from the catalogue.`,
    assess: (orders) => {
      const order = orders[0];

      if (order.state === 'Failed') {
        return { verdict: 'match', note: order.failureReason || 'Declined on amount; hold released.' };
      }

      return {
        verdict: 'mismatch',
        note: `The order ended ${order.state}. Either the basket did not exceed the limit, or payment-decline-over-amount is set higher than ${DEFAULT_DECLINE_LIMIT}.`,
      };
    },
  },
  {
    id: 'oversell-race',
    title: 'Oversell race',
    group: 'runtime',
    proves: 'Two orders chase the last unit at once. Exactly one wins — arbitrated by SQL, not C#.',
    alreadyHappened: 'Both orders ask for stock only one can have.',
    sagaDoes: 'One reserves and proceeds; the other is rejected and fails with nothing to undo.',
    expected: 'One order settles, one Failed.',
    build: oversellPair,
    unavailable: 'No SKU has stock to contend over.',
    assess: (orders) => {
      const failed = orders.filter((order) => order.state === 'Failed');
      const survived = orders.filter((order) => order.state !== 'Failed');

      if (failed.length === 1 && survived.length === 1) {
        return {
          verdict: 'match',
          note: 'Exactly one order got the stock; the other was rejected outright. The row-version predicate arbitrated, not application code.',
        };
      }

      if (failed.length === orders.length) {
        return {
          verdict: 'mismatch',
          note: 'Both orders failed — the winning basket may have tripped the amount limit too. Retry when a SKU is closer to sold out.',
        };
      }

      return {
        verdict: 'mismatch',
        note: 'Both orders succeeded, so they never actually contended — there was more stock than one unit.',
      };
    },
  },
];

const LEVER_SCENARIOS: readonly Scenario[] = [
  {
    id: 'payment-decline-all',
    title: 'Payment declined — every charge',
    group: 'lever',
    proves: 'The decline path independent of amount. The hold is released before the order goes terminal.',
    alreadyHappened: 'Stock is held; the charge is refused.',
    sagaDoes: 'ReleaseInventory, then fails the order.',
    expected: 'Failed, hold released.',
    lever: { command: 'dotnet run --project src/OrderFlow.AppHost -- --Parameters:payment-decline-all=true' },
    build: cheapestSingleLine,
    unavailable: 'No SKU has stock to buy.',
    assess: (orders) => expectFailure(orders, 'so payment-decline-all is not active.'),
  },
  {
    id: 'carrier-transient-recovering',
    title: 'Carrier flaky, then recovers',
    group: 'lever',
    proves: 'Retry with backoff actually recovers. The order goes through; the trouble shows only in telemetry.',
    alreadyHappened: 'Early dispatch attempts fail transiently.',
    sagaDoes: 'Retries within budget; the dispatch succeeds and the order confirms.',
    expected: 'Confirmed (after retries).',
    lever: { command: 'dotnet run --project src/OrderFlow.AppHost -- --Parameters:carrier-failure-mode=TransientRecovering' },
    build: cheapestSingleLine,
    unavailable: 'No SKU has stock to buy.',
    assess: (orders) =>
      expectStillConfirms(
        orders,
        'Confirmed despite transient carrier faults — the retries recovered. (A healthy carrier confirms too; the difference is in the trace, not here.)',
      ),
  },
  {
    id: 'carrier-transient-persistent',
    title: 'Carrier down, retries exhaust',
    group: 'lever',
    proves: '"Retryable" is not "retried forever". The bounded retries run out and it becomes a hard failure.',
    alreadyHappened: 'Stock is held AND the customer is charged.',
    sagaDoes: 'RefundPayment AND ReleaseInventory — the double compensation — then fails.',
    expected: 'Failed, refunded and released.',
    lever: { command: 'dotnet run --project src/OrderFlow.AppHost -- --Parameters:carrier-failure-mode=TransientPersistent' },
    build: cheapestSingleLine,
    unavailable: 'No SKU has stock to buy.',
    assess: (orders) => expectFailure(orders, 'so carrier-failure-mode is not TransientPersistent.'),
  },
  {
    id: 'carrier-permanent',
    title: 'Carrier rejects permanently',
    group: 'lever',
    proves: 'A permanent rejection is not retried at all — retrying a bad address just delays the refund.',
    alreadyHappened: 'Stock is held AND the customer is charged.',
    sagaDoes: 'RefundPayment AND ReleaseInventory immediately, then fails.',
    expected: 'Failed fast, refunded and released.',
    lever: { command: 'dotnet run --project src/OrderFlow.AppHost -- --Parameters:carrier-failure-mode=Permanent' },
    build: cheapestSingleLine,
    unavailable: 'No SKU has stock to buy.',
    assess: (orders) => expectFailure(orders, 'so carrier-failure-mode is not Permanent.'),
  },
  {
    id: 'notification-down',
    title: 'Notification provider down',
    group: 'lever',
    proves: 'Notification is best-effort. The order completes perfectly and the customer is simply never told.',
    alreadyHappened: 'The order is fully fulfilled; the notification send fails.',
    sagaDoes: 'Nothing — the failure is swallowed. The order stays Confirmed.',
    expected: 'Confirmed; the notification is dropped.',
    lever: { command: 'dotnet run --project src/OrderFlow.AppHost -- --Parameters:notification-provider-down=true' },
    build: cheapestSingleLine,
    unavailable: 'No SKU has stock to buy.',
    assess: (orders) =>
      expectStillConfirms(
        orders,
        'Confirmed. If the provider is down, the notification was dropped — best-effort by design. The drop shows in the Aspire logs and the Notification service, not on this screen.',
      ),
  },
  {
    id: 'notification-hangs',
    title: 'Notification provider hangs',
    group: 'lever',
    proves: 'A hanging provider must not hold the pipeline open. The send times out and the order still confirms.',
    alreadyHappened: 'The order is fully fulfilled; the notification send never returns.',
    sagaDoes: 'Times the send out — the pipeline is not held hostage. The order stays Confirmed.',
    expected: 'Confirmed; the send times out.',
    lever: { command: 'dotnet run --project src/OrderFlow.AppHost -- --Parameters:notification-provider-hangs=true' },
    build: cheapestSingleLine,
    unavailable: 'No SKU has stock to buy.',
    assess: (orders) =>
      expectStillConfirms(
        orders,
        'Confirmed. If the provider hangs, the send timed out rather than blocking the pipeline — visible in the trace, not here.',
      ),
  },
];
