# OrderFlow

An event-driven order-fulfillment reference architecture. Five .NET services, coordinated by a **saga with compensation** over an asynchronous message bus, orchestrated by .NET Aspire, with an Angular front end.

The happy path is the least interesting thing here. An order that reserves stock, charges a card, ships, and notifies the customer is a morning's work. This system exists to demonstrate what happens when **step three fails after step two has already taken the customer's money** — and to prove, with executable tests against real infrastructure, that the money comes back and the stock is released.

Everything runs locally on emulators. No cloud account, no spend, no keys to configure.

---

## What this is meant to demonstrate

| | |
|---|---|
| **Distributed consistency without 2PC** | A saga owns the workflow. Every forward step has a compensating action, and the ordering rule that makes compensation safe — *send the compensation before recording the terminal state* — is enforced in one class and asserted in the test suite. |
| **Idempotency that actually holds** | At-least-once delivery is assumed, not wished away. Every message carries a `MessageId` derived from what it *means* (`SHA256(orderId, messageType)`), so a redelivered command is recognised by the receiver rather than charging the customer twice. |
| **Failure paths as first-class features** | Retry with backoff, circuit breaking, dead-lettering, a recovery sweeper, and an ops view that can tell a hold that *shipped* from a hold that is *stranded*. |
| **Decisions recorded honestly** | Seven ADRs, each with a **Verification** section written *after* reading the code — including the places where the code and the document disagree, left as open items rather than quietly reworded. |
| **Right-sizing as an architectural act** | [ADR-004](docs/architecture%20decision%20records/4.md) exists to record a decision *not* to build something. |

---

## Architecture at a glance

![OrderFlow C4 container diagram](docs/design%20docs/diagrams/OrderFlow-C4-Container-Diagram.png)

Five independently deployable services. They share **one** project — `OrderFlow.Contracts`, which holds the message types — and nothing else. No service references another; they meet on the bus and never in the request path.

```
                       ┌──────────────────────────────────────────┐
   POST /api/Orders    │            Orders API (saga)             │
  ──────────────────▶  │  the only orchestrator in the system     │
                       └───┬───────────┬───────────┬──────────┬───┘
        ReserveInventory │  │ ChargePayment │  DispatchFulfillment │
        ReleaseInventory │  │ RefundPayment │                      │
         CommitInventory │  │               │                      │
                         ▼  ▼               ▼                      ▼
                    ┌─────────┐      ┌─────────┐          ┌──────────────┐
                    │Inventory│      │ Payment │          │ Fulfillment  │
                    │  (SQL)  │      │  (SQL)  │          │ (simulated   │
                    └────┬────┘      └────┬────┘          │   carrier)   │
                         │                │               └──────┬───────┘
       InventoryReserved │  PaymentSucceeded │  FulfillmentDispatched │
       InventoryRejected │  PaymentDeclined  │  FulfillmentFailed     │
                         └────────────┬─────┴────────────────────────┘
                                      ▼
                              back to the saga
                                      │
                                      ▼  OrderConfirmed / OrderFailed / PaymentDeclined
                              ┌───────────────┐
                              │ Notification  │   terminal subscriber — nothing replies to it
                              └───────────────┘
```

**Commands go to queues** (exactly one handler). **Events go to topics** (many subscribers). `PaymentDeclined` is the one event with two subscribers — the saga compensates, Notification informs — which is what earns the topic/subscription split its keep.

The compensation matrix the saga enforces:

| Failure | What has already happened | What the saga does |
|---|---|---|
| Inventory rejects | Nothing | Fail the order. **No compensation** — there is nothing to undo. |
| Payment declines | Stock is held | `ReleaseInventory`, then fail. |
| Carrier hard-fails | Stock is held **and** the customer is charged | `RefundPayment` **and** `ReleaseInventory`, then fail. |

A half-compensated order — refunded but not released, or released but not refunded — is worse than a failed one, because nothing downstream will ever notice. That is the failure this system is built to make impossible.

---

## Running it

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download), [Docker Desktop](https://www.docker.com/products/docker-desktop/) (running), [Node.js 20+](https://nodejs.org/).

```bash
git clone https://github.com/<your-org>/orderflow2026.git
cd orderflow2026
dotnet run --project src/OrderFlow.AppHost
```

That is the whole setup. Aspire starts SQL Server, the Cosmos DB emulator, Redis, and the Service Bus emulator in containers, runs `npm install` for the Angular workspace, and brings up the five services. The SQL administrator password is generated and persisted to user secrets on first run — there is no credential in this repository and nothing for you to set.

First run is slow (the Cosmos emulator especially). Subsequent runs reuse the volumes.

The Aspire dashboard opens automatically. It is also the observability surface: every order produces **one distributed trace spanning all five services**, because consumers re-parent onto the producer's trace context rather than starting an orphan span per hop.

| Surface | Where |
|---|---|
| Aspire dashboard — logs, traces, resource health | opens on `dotnet run` |
| Customer order view | http://localhost:4200 |
| Ops view — active orders, stuck orders, dead letters | http://localhost:4200/ops |
| OpenAPI per service | each service's `/scalar` endpoint, linked from the dashboard |

### API surface

| Service | Endpoint | |
|---|---|---|
| **Orders** | `POST /api/Orders` | Place an order. SKU and quantity only — [the client does not send a price](docs/architecture%20decision%20records/6.md). |
| | `GET /api/Orders/{id}` | Current status, from the Redis projection. |
| | `GET /api/Orders/{id}/timeline` | The full event stream, replayed from Cosmos. |
| | `GET /api/Orders/active` | Non-terminal orders. |
| | `GET /api/Orders/stuck` | Orders that have stopped moving — the same query the recovery sweeper runs. |
| | `GET /api/Orders/dead-letters` | Messages that could not be processed. |
| | `POST /api/Orders/rebuild-projection` | Replays every event stream and rebuilds the read model from scratch. |
| **Inventory** | `GET /api/Inventory` | On-hand, reserved, available, and unit price per SKU. |
| | `GET /api/Inventory/reservations/{orderId}` | What is held for one order. |
| **Payments** | `GET /api/Payments/order/{orderId}` | Payment attempt history, including retries. |
| **Fulfillment** | `GET /api/Fulfillment/stuck` | Dispatches the carrier never gave an answer to. |
| **Notification** | `GET /api/Notification` | Everything sent, and everything dropped. |

---

## Breaking it on purpose

Every failure scenario is a lever on the AppHost, not an edit to the code. Set one, restart, and a specific row of the failure matrix fires.

```bash
dotnet run --project src/OrderFlow.AppHost -- --Parameters:payment-decline-all=true
```

| Lever | Values | What it proves |
|---|---|---|
| `payment-decline-all` | `true` / `false` | The decline path: the inventory hold comes back. |
| `payment-decline-over-amount` | decimal (default `1000`) | The same, triggered by order value rather than globally. |
| `carrier-failure-mode` | `None`, `TransientRecovering`, `TransientPersistent`, `Permanent` | Retry with backoff; the circuit breaker; and the double compensation — **refund *and* release**. |
| `notification-provider-down` | `true` / `false` | Notification is best-effort. The order completes perfectly and the customer is simply never told. |
| `notification-provider-hangs` | `true` / `false` | The send timeout. A hanging provider must not hold the pipeline open. |

The oversell race needs no lever — just two clients buying the last unit at once. Exactly one wins, arbitrated by a SQL Server row-version predicate, not by C#.

---

## Tests

```bash
dotnet test
```

**42 unit tests.** They read as a specification of the failure paths, because that is what they are:

```
PaymentDeclined_releases_the_hold_BEFORE_the_order_becomes_terminal
FulfillmentFailed_refunds_the_payment_AND_releases_the_hold
A_redelivered_PaymentDeclined_does_not_release_the_stock_twice
ChargePayment_uses_the_price_Inventory_returned_not_one_the_customer_sent
A_charge_with_NO_idempotency_key_throws_because_it_can_never_be_made_safe
A_BROKEN_CIRCUIT_is_NOT_a_hard_failure_and_must_propagate
```

**5 integration tests**, each booting the *real* distributed application — SQL, Cosmos, Redis, and Service Bus in containers, all five services wired against them — and driving it through the public API, with failure injected through the same AppHost levers an operator would use.

They exist because some of these guarantees **cannot be unit-tested honestly**. The oversell guard is made by SQL Server's row-version predicate; the double-charge guard by a unique index. EF Core's InMemory provider enforces neither. A green test against it would have *implied* both guards worked while proving nothing of the sort — so those two are proven against real databases, or not claimed at all.

---

## Documentation

**Start here:** [**docs/ARCHITECTURE.md**](docs/ARCHITECTURE.md) — a full walkthrough of the domain, the requirements, the message flow, the layering, and how to read the code.

### Architecture Decision Records

Each ADR carries a **Verification** section written after re-reading the implementation. Where the code and the document disagree, the disagreement is recorded as an open item rather than quietly edited away — ADR-002 corrects its own operation count, and ADR-004 concludes that its own wording is wrong and the code is right.

| | Decision | |
|---|---|---|
| [ADR-001](docs/architecture%20decision%20records/1.md) | **Saga with compensation** for the order workflow | Why not 2PC. What "undo" actually means when it is semantic, not transactional. |
| [ADR-002](docs/architecture%20decision%20records/2.md) | **Cosmos DB** for the append-only order event log | Partitioned by `OrderId`. Why `CreateItem`, never `Upsert` — an upsert turns an audit trail into a mutable record. |
| [ADR-003](docs/architecture%20decision%20records/3.md) | **Light CQRS** read model in Redis | The projection is disposable *because* it can be rebuilt. It records the period when that claim was false. |
| [ADR-004](docs/architecture%20decision%20records/4.md) | **No** separate warehouse-management service | A decision not to build something. Right-sizing as an architectural act. |
| [ADR-005](docs/architecture%20decision%20records/5.md) | **Aspire-local emulation, real Azure SDKs** | Going live is a config change, not a rewrite — and why zero fakes survived contact with the test suite. |
| [ADR-006](docs/architecture%20decision%20records/6.md) | **The catalogue prices the order, not the customer** | The client used to send `UnitPrice`. You could buy a laptop for a penny. The field is gone. |
| [ADR-007](docs/architecture%20decision%20records/7.md) | **A recovery sweeper instead of an outbox** | The one failure that was both silent and permanent, and the cheapest honest fix for it. |

### Design

| | |
|---|---|
| [High-Level Design](docs/design%20docs/high%20level%20design/Order%20Flow%20HLD.pdf) | The full design document — context, containers, data, failure model. |
| [C4 container diagram](docs/design%20docs/diagrams/OrderFlow-C4-Container-Diagram.png) · [container interactions](docs/design%20docs/diagrams/OrderFlow-C4-Container-Interactions.png) · [sequence diagram](docs/design%20docs/diagrams/OrderFlow-Sequence-Diagram.png) | |
| [User stories](docs/user%20stories/userstories.md) | The requirements, including the ops and architecture proof-points. |
| [Delivery & Investment Plan](docs/decks/Delivery_Investment_Plan.pdf) | How this would be delivered as an engagement — phasing, cost, and risk. |

---

## Technology

.NET 10 · C# 14 · ASP.NET Core · .NET Aspire 13.3 · Azure Service Bus · Cosmos DB · SQL Server + EF Core 10 · Redis · Polly · OpenTelemetry · Angular 20 (standalone, signals) · xUnit

Every service is layered the same way — `Controller`/`Consumer` → `Facade` → `Business` → `Data` → `DbContext` — with hand-written mapping extensions and no reflection-based mapper. Learn one service and you have learned all five.

---

## A note on scope

This is a reference architecture, not a product. There is no real payment processor, no real carrier, and no real PII. The catalogue does not quote a price before checkout. There is no auth.

Those absences are deliberate and they are documented — the ADRs say what was left out and why, and the ones that would be genuinely wrong in production say so plainly. What is here is the part that is hard: **five services staying consistent with each other while things fail.**

---

Built by **Robert Felkins** as a working demonstration of distributed-systems design in .NET — the kind of engagement where correctness under failure is the requirement, not a stretch goal.

If you are evaluating this repository as part of a hiring or contracting decision, the fastest read is [ADR-001](docs/architecture%20decision%20records/1.md) (the reasoning), [`OrderSaga.cs`](src/OrderFlow.Orders.API/Managers/Saga/OrderSaga.cs) (the implementation), and [`OrderSagaTests.cs`](tests/OrderFlow.UnitTests/OrderSagaTests.cs) (the proof).

📧 robert@architect4hire.com
