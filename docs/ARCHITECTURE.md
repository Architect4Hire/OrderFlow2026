# OrderFlow — Architecture & Implementation Walkthrough

This document walks a developer from the problem, through the design, into the code. It assumes you have read the [README](../README.md) and want to know *how it actually works* — and, more usefully, *why each piece is the shape it is*.

Read it end to end and you should be able to find any behaviour in the codebase, and predict what the system does when any single component fails.

---

## Contents

1. [The problem](#1-the-problem)
2. [Requirements](#2-requirements)
3. [The architecture](#3-the-architecture)
4. [The message flow](#4-the-message-flow)
5. [The saga, in detail](#5-the-saga-in-detail)
6. [The four guarantees, and where each one is made](#6-the-four-guarantees-and-where-each-one-is-made)
7. [Service internals](#7-service-internals)
8. [How a service is layered](#8-how-a-service-is-layered)
9. [Observability](#9-observability)
10. [Testing strategy](#10-testing-strategy)
11. [What was deliberately left out](#11-what-was-deliberately-left-out)
12. [The path to production](#12-the-path-to-production)

---

## 1. The problem

An order touches four services in sequence. Inventory reserves the stock, Payment charges the card, Fulfillment dispatches to a carrier, Notification tells the customer. Each service owns its own data store. There is no shared database.

So there is no transaction. You cannot `BEGIN`, reserve, charge, ship, and `COMMIT`. The moment stock is reserved in Inventory's database, that fact is *committed and visible* — and if the charge is then declined, no rollback exists that can take it back. You have to go and *ask* Inventory to release it, in a separate transaction, and hope you can.

That "and hope you can" is the entire subject of this system.

Consider the worst case. The carrier permanently rejects the shipment. By that point:

- Inventory has **held stock** for this order.
- Payment has **captured money** from the customer.

Two undo operations are now owed, in two different services, and they must *both* happen. If the refund goes out and the release does not, that stock is invisible and unsellable and nothing will ever notice. If the release goes out and the refund does not, you have taken money for an order that will never ship.

A half-compensated order is worse than a failed one, because a failed order is *visible* and a half-compensated one looks fine.

Everything below exists to make that impossible.

---

## 2. Requirements

The full list is in [user stories](user%20stories/userstories.md). The ones that shape the architecture:

**Consistency**
- Never charge for stock we do not have. *(Reserve before charge.)*
- Never leave stock reserved for an order that will never ship. *(Compensate on every failure path.)*
- Never oversell. Two customers racing for the last unit — exactly one wins, cleanly.
- Never double-charge, even if a message is delivered twice.

**Recoverability**
- A service can be killed mid-workflow and the order resumes correctly on restart.
- A transient carrier failure is retried; a permanent one is a business outcome, not a crash.
- Anything the system genuinely cannot process ends up somewhere a human can see it.

**Observability**
- One distributed trace per order, spanning all five services.
- An ops view that can distinguish a hold that *shipped* from a hold that is *stranded*.

**Boundaries**
- Notification is best-effort. A notification failure must never roll back an order.
- Services share message contracts and nothing else.

Notice how many of those are stated as *never*. They are safety properties, and a safety property is only worth as much as the test that proves it. Section 10 is where they get proved.

---

## 3. The architecture

![C4 container diagram](design%20docs/diagrams/OrderFlow-C4-Container-Diagram.png)

### Five services

| Service | Owns | Store |
|---|---|---|
| **Orders** | The order aggregate, the event log, the saga. **The only orchestrator.** | Cosmos DB (event log) + Redis (read model) |
| **Inventory** | The catalogue, stock levels, reservations. **The price.** | SQL Server |
| **Payments** | Charges and refunds. | SQL Server |
| **Fulfillment** | The resilient call to a simulated external carrier. | *(stateless)* |
| **Notification** | Customer-facing messages. Terminal subscriber. | *(in-memory record)* |

### One shared project

`OrderFlow.Contracts` holds the message types — six commands, nine events, and a `MessageBase` that carries `MessageId`, `CorrelationId` (always the `OrderId`), and `OccurredUtc`.

**No service references another service.** That rule is what makes the rest of the design honest: if Orders could call Inventory over HTTP, the temptation to make the workflow synchronous would win eventually, and every argument in [ADR-001](architecture%20decision%20records/1.md) would evaporate. Services meet on the bus. They never meet in the request path.

### Polyglot persistence, and why

Three stores, because the three workloads genuinely differ:

- **Cosmos DB** — the append-only order event log, partitioned by `OrderId` ([ADR-002](architecture%20decision%20records/2.md)). Append-heavy, read by single key, never updated. One order's whole history lives in one partition, so replaying it is a single-partition read.
- **Redis** — the order status projection ([ADR-003](architecture%20decision%20records/3.md)). The customer view polls every couple of seconds; replaying an event stream per poll would be absurd. This is a *cache of current state*, not a source of truth, and it can be rebuilt from Cosmos at any time.
- **SQL Server** — inventory and payments. Rows that get *updated* under contention, with constraints that must hold: a row-version predicate to arbitrate the oversell race, a unique index to arbitrate the double-charge race. These are relational problems and SQL Server is very good at them.

The cost is real — two database technologies, two operational stories — and [ADR-002](architecture%20decision%20records/2.md) records it as an accepted negative rather than pretending it is free.

### Local by default, real by SDK

Everything runs on emulators, orchestrated by Aspire ([ADR-005](architecture%20decision%20records/5.md)): SQL Server in a container, the Cosmos emulator, Redis, the Service Bus emulator.

But the *code* is written against the **real Azure SDKs** — `Azure.Messaging.ServiceBus`, the Cosmos SDK, `StackExchange.Redis`, EF Core. There is no hand-rolled fake bus and no in-memory store anywhere in `src/`. Going live is pointing the SDK at a live namespace. It is a config change, not a rewrite — and that claim is credible precisely *because* no service names a broker, a database, or a connection string anywhere in its code.

Start at [`src/OrderFlow.AppHost/Program.cs`](../src/OrderFlow.AppHost/Program.cs). It is the wiring diagram of the whole system, and it is commented as one.

---

## 4. The message flow

**Commands go to queues. Events go to topics.**

That is not decoration. A command is an instruction to one service to do one thing — `ChargePayment` has exactly one correct handler, so it goes on a queue. An event is a statement of fact that anyone may care about, so it goes on a topic with one subscription per interested service.

The distinction earns its keep on exactly one message. `PaymentDeclined` has **two** subscribers: the saga, which must compensate, and Notification, which must tell the customer. A queue physically cannot deliver to both. Every other event happens to have one subscriber today — but the moment a second one is needed, no infrastructure change is required.

### Naming

Message type → entity name is mechanical, and lives in exactly one place ([`MessagingConventions`](../src/OrderFlow.ServiceDefaults/Messaging/MessagingConventions.cs)):

```
ReserveInventory   →  reserve-inventory   (queue)
InventoryReserved  →  inventory-reserved  (topic)
```

The producer resolves it to pick a destination; the consumer resolves it to pick a subscription. Both call the same function, so renaming a contract breaks both ends *together* rather than silently routing messages into the void.

### The happy path

```
POST /api/Orders
  │
  │  OrderBusinessManager.PlaceAsync:
  │    1. append OrderPlaced to the Cosmos event log
  │    2. project the order into Redis
  │    3. send ReserveInventory
  ▼
[reserve-inventory] ──▶ Inventory
  │                       holds stock, reads the price off the StockItem rows
  ▼
InventoryReserved (carrying the PRICED lines and the order total)
  │
  ▼ saga: stamp the prices onto the aggregate, then charge
[charge-payment] ─────▶ Payment
  │                       charges order.Total, keyed by an idempotency key
  ▼
PaymentSucceeded
  │
  ▼ saga
[dispatch-fulfillment] ▶ Fulfillment
  │                       calls the carrier through a Polly pipeline
  ▼
FulfillmentDispatched
  │
  ▼ saga: commit the hold, then confirm
[commit-inventory] ────▶ Inventory     (the goods left the building —
  │                                      Reserved AND OnHand both fall)
  ▼
OrderConfirmed ────────▶ Notification
```

Note step 1 of `PlaceAsync`: the order is **appended to the event log before the command is sent**. That ordering is deliberate and it is load-bearing — see §6.4.

### Where the price comes from

Look again at `InventoryReserved`. It carries **priced lines and a total**.

The original design had `UnitPrice` on the incoming `PlaceOrderViewModel`. The client sent it, the domain copied it, and the saga charged it. Which means **the customer set the price they were charged** — a caller could buy a laptop for a penny by editing one field of the JSON they were already sending, and every service downstream would have faithfully carried the number, because every service was written to trust its input.

There was no validation that could have caught it, either, because there was nothing to validate *against*: **no service in the system knew what anything cost.** The price was missing from the domain entirely.

The fix ([ADR-006](architecture%20decision%20records/6.md)) is structural, not defensive. Inventory owns the catalogue, so Inventory owns the price. `StockItem` gains a `UnitPrice`; the ViewModel *loses* one. The client sends a SKU and a quantity, which is all it is entitled to assert. The price is read off the row at the moment the hold is taken, in the service that owns it, and travels back on the reply the saga was already waiting for.

The attack is not mitigated. It is **gone** — a field that does not exist cannot be forged.

The accepted cost: between `OrderPlaced` and `InventoryReserved` an order genuinely has a total of £0, and the status view will show it. That looks like a bug and is not. The customer has said what they want; nobody has yet said what it costs.

---

## 5. The saga, in detail

[`src/OrderFlow.Orders.API/Managers/Saga/OrderSaga.cs`](../src/OrderFlow.Orders.API/Managers/Saga/OrderSaga.cs) — read this file. It is the system.

Every handler has the same shape: **one event in, one decision out.**

```csharp
public async Task OnPaymentDeclinedAsync(PaymentDeclined declined, CancellationToken ct)
{
    var order = await LoadAsync(declined.CorrelationId, nameof(PaymentDeclined), ct);
    if (order is null) return;                       // terminal guard — see below

    await ApplyAsync(order, declined, order.State, ct, declined.Reason);

    await ReleaseInventoryAsync(order, declined.Reason, ct);   // COMPENSATE first...
    await FailAsync(order, declined.Reason, ct);               // ...THEN go terminal
}
```

Three properties are doing all the work here.

### 5.1 State is rehydrated, never held

`LoadAsync` replays the order's Cosmos event stream on **every** handler invocation. The saga holds no state in a field, no state in memory, no state in a static.

That is what lets the Orders service be killed mid-saga and resume correctly. It is also why Redis can be flushed without losing an order: Redis is a projection, and the event log is the truth.

The fold that turns a stream into an `Order` lives in [`OrderRehydrator`](../src/OrderFlow.Orders.API/Managers/Saga/OrderRehydrator.cs), and the projection rebuild uses **the same fold**. Deliberately. Two independent interpretations of an event log will eventually disagree, and then the ops view confidently reports a state the saga does not believe in.

### 5.2 The terminal guard

`LoadAsync` returns `null` if the order is already `Confirmed` or `Failed`. The handler returns immediately.

This is what makes a redelivered event a no-op: no second refund, no second release, no second email. At-least-once delivery is *assumed*, so this guard is not an optimisation — it is the thing standing between a duplicate message and a duplicate refund.

### 5.3 The ordering rule — the whole game

> **Every outbound message is sent BEFORE the terminal state is recorded.**

The terminal guard cuts both ways, and this is the subtlety worth slowing down for.

Suppose you record `Failed` first, and *then* try to send `ReleaseInventory`, and that send throws. The message is redelivered. The handler runs again. It calls `LoadAsync` — which finds a **terminal** order, returns `null`, and the handler no-ops.

The release is never sent. The reservation is stranded forever. Silent, permanent stock loss — the exact bug this entire system exists to prevent, caused by the guard that was supposed to protect it.

So on every failure path, the compensations go out **first**, and only once they are away does the order become terminal. Crash anywhere before that point and the whole handler replays from a non-terminal state, which re-sends the compensation. A duplicate `ReleaseInventory` is harmless — Inventory is idempotent. A *missing* one is not.

The same rule applies to `OrderConfirmed` and `OrderFailed`: published before the terminal append, because a failed publish after it would be swallowed by the guard on retry and the customer would never be told.

Both orderings are asserted in the test suite, by name:

```
PaymentDeclined_releases_the_hold_BEFORE_the_order_becomes_terminal
FulfillmentFailed_sends_both_compensations_BEFORE_the_order_becomes_terminal
OrderConfirmed_is_published_BEFORE_the_order_becomes_terminal
```

### 5.4 The state machine

```
                  InventoryRejected
        ┌──────────────────────────────────────────┐
        │                                          ▼
     Placed ──InventoryReserved──▶ Reserved ──▶ Failed ◀── PaymentDeclined
                                      │            ▲            (+ ReleaseInventory)
                              PaymentSucceeded     │
                                      │            │
                                      ▼            │
                                    Paid ──────────┘  FulfillmentFailed
                                      │               (+ RefundPayment
                              FulfillmentDispatched    + ReleaseInventory)
                                      │
                                      ▼
                                 Dispatched ──▶ Confirmed
                                (+ CommitInventory)
```

`Confirmed` and `Failed` are terminal. Everything else is in flight, and everything in flight is on the ops list.

---

## 6. The four guarantees, and where each one is made

### 6.1 Idempotency — *a message delivered twice does not act twice*

Two halves, and both are necessary.

**The receiving half** lives in [`ServiceBusConsumer<TMessage>`](../src/OrderFlow.ServiceDefaults/Messaging/ServiceBusConsumer.cs), the base class every consumer in the system inherits. Before a handler runs, it checks a durable store for the key **`(ConsumerName, MessageId)`**.

Not `MessageId` alone. One event fans out to several subscribers, and keyed on the message alone, whichever consumer ran first would suppress the others — the saga would compensate and the customer would never be told, or the reverse. And `ConsumerName` is qualified by the *service*, not just the class, because the saga and Notification **both** have a `PaymentDeclinedConsumer`, and `payment-declined` is precisely the topic with two subscribers.

**The sending half** is the more interesting one. A guard on `MessageId` is worthless if every retry mints a fresh `MessageId` — the duplicate simply looks new. So every message the saga emits carries a **deterministic** id:

```csharp
DeterministicMessageId(orderId, nameof(RefundPayment))
    => SHA256($"{orderId:N}:{discriminator}")[0..16]
```

The same `(order, message type)` pair *always* yields the same id. A replayed handler re-sends a message the receiver has already seen, and the receiver drops it. Mint a fresh `Guid` here instead and every retry becomes a second refund.

The stores are durable, not in-process: Cosmos for Orders (partitioned by `/consumerName`, so the check is a point read and an insert collision *is* the duplicate detection), SQL for Inventory and Payments. An in-memory set would forget every processed message on restart — which is exactly when you need it.

### 6.2 Never oversell — *SQL makes this promise, not C#*

[`InventoryData`](../src/OrderFlow.Inventory.API/Managers/Data/InventoryData.cs). `StockItem` has a `RowVersion`, configured as an EF concurrency token, so the UPDATE EF emits is:

```sql
UPDATE StockItems SET Reserved = @new, UpdatedUtc = @now
 WHERE Sku = @sku AND RowVersion = @loaded    -- ← the whole guarantee
```

Two requests read `Available = 1`. Both decide they can satisfy the order. Both write. **One of them updates zero rows**, EF raises `DbUpdateConcurrencyException`, and the loser reloads the winner's numbers and re-decides — at which point `Available` is 0 and it rejects cleanly.

No lock. No serializable transaction. No lost update.

Note the three verbs and their different meanings, which is a distinction the ops view depends on:

| | `Reserved` | `OnHand` | Meaning |
|---|---|---|---|
| **Reserve** | ↑ | — | Held. Still on the shelf, but spoken for. |
| **Release** | ↓ | — | The hold is given back. The goods never left. |
| **Commit** | ↓ | ↓ | The goods **shipped**. Take them off the shelf for good. |

Without `CommitInventory` the reservation would stay `Held` forever after a successful shipment, `OnHand` would never fall, and the warehouse row would permanently overstate the shelf. Worse: the ops view could no longer tell a hold that *shipped* from a hold that is *stranded by a lost compensation* — and telling those two apart is the entire point of the ops view.

### 6.3 Never double-charge — *the same shape, one layer up*

`ChargePayment` carries an `IdempotencyKey` (the order id), stable across every retry. Payment resolves that key to a row behind a unique index. A duplicate charge **returns the first outcome and never asks the processor again** — including when the first outcome was a *decline*, in which case the duplicate gets the decline and its original reason.

The concurrent case is covered too: if two charges race and one loses the insert, it reads back and returns the **winner's** outcome rather than proceeding.

And a `ChargePayment` with no idempotency key **throws**, because a charge that cannot be made safe must not be attempted at all.

### 6.4 Nothing is lost — *four mechanisms, for four different failures*

They are not interchangeable, and knowing which one handles which failure is most of understanding this system.

**(a) Handler throws → redelivery.** `AutoCompleteMessages` is **off**. Nothing is completed until the handler has succeeded. A handler that throws leaves the message unsettled: it is abandoned, redelivered, and after `MaxDeliveryCount` attempts it dead-letters. Completing first — the SDK default, and the easy mistake — turns every transient fault into a silently dropped order.

This is also what makes "publish the reply *inside* the handler" sufficient: if the publish throws, the command is never settled, so the whole thing is retried and the sender is never left waiting on a reply that no longer exists.

**(b) Cannot be processed → dead-letter.** A poison message (unparseable id, undeserializable body) dead-letters **immediately** — retrying it cannot help, so it does not get to burn the delivery count first. Anything else that exhausts its deliveries dead-letters too. `GET /api/Orders/dead-letters` is the ops surface.

**The distinction that matters most here, and the one [ADR-004](architecture%20decision%20records/4.md) admits its own wording got wrong:** a carrier *hard failure* is **not** a dead-letter. Retries exhausted, or a permanent rejection, means we **asked and got a final answer**, and the answer was no. That message has been *processed*. The consumer publishes `FulfillmentFailed` and **completes** it, the saga refunds and releases, and the order dies cleanly.

Dead-lettering it instead would mean the saga is never told anything at all: money captured, stock held, order frozen at `Paid` forever, and nothing on any screen to say why. **A business outcome is not a delivery failure.** Dead-lettering is reserved for failures we could not get an answer to.

**(c) Transient dependency failure → Polly.** [`CarrierClient`](../src/OrderFlow.Fulfillment.API/Managers/Business/CarrierClient.cs) wraps the carrier in a bounded retry with exponential backoff, plus a circuit breaker. Retry and breaker share **one predicate** — `TransientCarrierException` — so a `PermanentCarrierException` falls straight through untouched. A rejected address does not get better because you asked four more times.

**(d) The one failure none of the above could catch → the recovery sweeper.** This is the good one.

Everything above recovers because it is *driven by a message*. `PlaceAsync` is not. It is driven by HTTP and it gets exactly one attempt: append `OrderPlaced`, project to Redis, send `ReserveInventory`.

**If that send throws, the order exists, is `Placed`, is on the active list — and nothing is listening to it, and nothing ever will be.** No message was lost, so no dead-letter queue shows anything. The customer sees a 500, re-posts, and gets a *second* order. It is the only failure in the system that is both silent and permanent, and it sits at the very front door.

The textbook fix is a transactional outbox. That is correct, and it is a table, a dispatcher process, a lease protocol, and a delivery-ordering story — a lot of machinery for a POC whose subject is compensation, not delivery guarantees.

So instead ([ADR-007](architecture%20decision%20records/7.md)): a `BackgroundService` that periodically finds non-terminal orders that have stopped moving and **re-sends the command their current state is waiting on**.

```
Placed    →  re-send ReserveInventory
Reserved  →  re-send ChargePayment
Paid      →  re-send DispatchFulfillment
```

What makes this safe is a property the system already had. **Every command carries a deterministic `MessageId`** (§6.1). So a command that *did* get through is deduped by the receiver and re-sending it costs nothing; a command that never got sent is simply sent. **The sweeper does not need to know which case it is in** — and that is the entire trick.

It costs one class. It generalises to a lost `ChargePayment` or `DispatchFulfillment`, not just the front-door case it was built for. And it gives the ops view something true to show: `GET /api/Orders/stuck` is the same query the sweeper runs.

It is honestly *not* an outbox and does not pretend to be. There is still a window where the event is appended and the command is not sent; the sweeper closes it *eventually* (within about a minute), not atomically. It also **deliberately will not touch an order whose reply event dead-lettered** — that failure is already *visible*, and it wants a human, not a retry loop. A sweeper that quietly re-drove dead-lettered work would destroy the evidence the ops view exists to show.

Recovery and diagnosis are different jobs.

---

## 7. Service internals

### Orders — the orchestrator

The only service with two stores and the only one that orchestrates.

| Component | Job |
|---|---|
| [`OrderSaga`](../src/OrderFlow.Orders.API/Managers/Saga/OrderSaga.cs) | The workflow. Six handlers, one per reply event. |
| [`OrderRehydrator`](../src/OrderFlow.Orders.API/Managers/Saga/OrderRehydrator.cs) | Stream → `Order`. Shared by the saga and the projection rebuild. |
| [`OrderEventStore`](../src/OrderFlow.Orders.API/Managers/DataContext/OrderEventStore.cs) | Append-only Cosmos. `CreateItem`, **never** `Upsert` — an upsert would silently overwrite an event and quietly turn the audit trail into a mutable record. |
| [`OrderReadModel`](../src/OrderFlow.Orders.API/Managers/Data/OrderReadModel.cs) | The Redis projection. The `orders:active` set is maintained in a `MULTI`/`EXEC` alongside the document, so an order can never be `Confirmed` *and* still on the ops in-flight list. |
| [`OrderRecoveryManager`](../src/OrderFlow.Orders.API/Managers/Business/OrderRecoveryManager.cs) | The sweeper (§6.4d). |
| [`OrderOpsManager`](../src/OrderFlow.Orders.API/Managers/Business/OrderOpsManager.cs) | Timeline, dead letters, and the projection rebuild. |

**Event ordering.** Each event carries a per-stream monotonic `Sequence`, and the Cosmos document id is `{orderId:N}-{sequence:D4}`. The store originally sorted by `OccurredUtc`, which *cannot* order a stream reliably — two events written in the same clock tick tie, and the tie is broken arbitrarily. Rehydration order determines state, so an order could replay as Paid-then-Reserved. Putting the sequence in the document key also makes concurrent appends **collide** rather than interleave.

**The rebuild.** `POST /api/Orders/rebuild-projection` replays every stream and re-projects it. This is the one operation that deliberately fans out across Cosmos partitions — which is why it is an operator-triggered POST and not something on a timer. It is also what makes [ADR-003](architecture%20decision%20records/3.md)'s central claim true rather than aspirational: the read model is disposable *because* it can be rebuilt. (That ADR carries an amendment recording the period when it could not be, and the ops list therefore stayed permanently empty after a Redis flush while orders were genuinely in flight.)

### Inventory — the catalogue and the race

Owns `StockItem` (sku, on-hand, reserved, **unit price**, row-version) and `Reservation`. Covered in §6.2 and §4.

Reservation is **all-or-nothing across lines**: if a later line cannot be held, the earlier lines are released before rejecting. Insufficient stock and an unknown SKU are both *rejections* — normal business answers, returned as `InventoryRejected` — not exceptions.

### Payments — idempotency as a first-class concern

Covered in §6.3. The processor itself is [`SimulatedPaymentAuthorizer`](../src/OrderFlow.Payments.API/Managers/Business/SimulatedPaymentAuthorizer.cs), driven by the AppHost's decline levers.

### Fulfillment — resilience as the whole job

The one service with no database. Its entire reason to exist is the resilient outbound call (§6.4c) and the correct classification of its outcomes (§6.4b).

[ADR-004](architecture%20decision%20records/4.md) records the decision **not** to decompose this into a warehouse-management service. Pick/pack/bin-location internals would add surface without adding insight, and the seam as drawn is honest: dispatch is a call to something we do not own, which is exactly how it behaves in production.

### Notification — the boundary that must not leak

Notification is **best-effort**, and it takes that seriously enough to be slightly paranoid:

- A provider that is **down** drops the notification and **never throws**.
- A provider that **hangs** is timed out rather than holding the pipeline open.
- An **unexpected** failure is also swallowed — because a bug here must not disturb a finished order.
- Failures are retried a bounded number of times, then given up on and *recorded as given up on*.

The integration test that pins this down is named for exactly what it asserts: `An_order_completes_perfectly_even_though_the_customer_is_never_told`.

---

## 8. How a service is layered

Every service is the same shape, so learning one teaches you all five.

```
Controller  (HTTP in)  ─┐
                        ├──▶  Facade  ──▶  Business  ──▶  Data  ──▶  DbContext
Consumer    (bus in)   ─┘        │            │
                                 │            └── domain rules, no EF types
                                 └── the service's public surface
```

Inside each service, `Managers/` holds `DataContext/`, `Domain/`, `ViewModels/`, `ServiceModels/`, `Extensions/`, `Data/`, `Business/`, `Facades/`, `Consumers/`.

The rules, which are enforced consistently:

| Rule | Why |
|---|---|
| **ViewModel** = incoming HTTP. **ServiceModel** = outgoing HTTP. **Domain** = internal. | A domain entity never leaves the service. Change the model without breaking the API; change the API without touching the model. |
| EF Core types never appear above `Data`. | Controllers, Consumers, Facades, and Business layers do not know what a `DbContext` is. |
| Mapping is hand-written extension methods. | No AutoMapper, no reflection-based mapper. A mapping bug is a compile error or a visible line of code, not a runtime surprise. |
| Everything is `async` with a `CancellationToken`. | |
| Public Facade, Controller, and Consumer methods carry XML doc comments. | |

Message consumers get all of §6.1 and §6.4a **for free** by deriving from `ServiceBusConsumer<TMessage>`. A consumer implements one method:

```csharp
protected abstract Task HandleAsync(IServiceProvider services, TMessage message, CancellationToken ct);
```

It runs inside a fresh DI scope, *after* the idempotency guard has cleared it. **Throw to retry. Return to settle.** That is the entire contract, and it means no individual consumer can get settlement or deduplication wrong — because no individual consumer implements either.

---

## 9. Observability

`OrderFlow.ServiceDefaults` wires OpenTelemetry, health checks, and service discovery into every service.

**One trace per order, across all five services.** The publisher stamps the W3C trace context onto the Service Bus message; the consumer reads it back and **re-parents** its activity onto it. Without that, every hop starts an orphan span and the end-to-end trace — the thing that actually proves the saga works — quietly falls apart into five unrelated fragments.

**Health:** `/health` is readiness (every check must pass); `/alive` is liveness. The distinction matters at the boundary: a consumer that cannot attach to its queue retries for 90 seconds and then **takes the host down deliberately**, rather than sitting there reporting Healthy while messages pile up in a queue nobody is reading. A service that cannot read its own queue has no business claiming to be up.

**Ops surfaces**, all backed by the same data the system uses to make decisions:

| | |
|---|---|
| `GET /api/Orders/{id}/timeline` | The order's real event stream, from the log. |
| `GET /api/Orders/stuck` | The same query the recovery sweeper runs. |
| `GET /api/Orders/dead-letters` | What could not be processed, and why. |
| `GET /api/Fulfillment/stuck` | Dispatches the carrier never answered. |
| `GET /api/Inventory` | On-hand vs reserved vs available, per SKU. |

---

## 10. Testing strategy

The split is not "unit tests fast, integration tests slow." It is: **some of these guarantees cannot be unit-tested honestly.**

### 42 unit tests — the decisions

Fast, in-memory fakes, covering every decision the code makes. They read as a specification because they are one:

```
FulfillmentFailed_refunds_the_payment_AND_releases_the_hold
FulfillmentFailed_sends_both_compensations_BEFORE_the_order_becomes_terminal
A_redelivered_FulfillmentFailed_does_not_refund_twice
An_event_arriving_after_the_order_is_Confirmed_is_ignored
The_saga_reads_its_state_from_the_event_store_so_a_restart_resumes_correctly
A_failed_compensation_send_surfaces_so_the_message_retries
If_a_LATER_line_cannot_be_held_the_EARLIER_lines_are_released
A_charge_with_NO_idempotency_key_throws_because_it_can_never_be_made_safe
A_BROKEN_CIRCUIT_is_NOT_a_hard_failure_and_must_propagate
```

### 5 integration tests — the guarantees the database makes

Each boots the **real** distributed application through `DistributedApplicationTestingBuilder` — SQL, Cosmos, Redis, and Service Bus in containers, all five services wired against them — and drives it through the public HTTP API.

| Test | What it proves |
|---|---|
| `An_order_is_reserved_charged_dispatched_and_confirmed` | The happy path, end to end. |
| `Two_concurrent_orders_for_the_last_unit_produce_exactly_one_winner` | **No oversell.** |
| `A_declined_payment_releases_the_inventory_hold_and_fails_the_order` | Compensation. |
| `A_permanent_carrier_failure_refunds_the_payment_AND_releases_the_hold` | **Double** compensation. |
| `An_order_completes_perfectly_even_though_the_customer_is_never_told` | The Notification boundary holds. |

Two design choices in here are worth stealing.

**Failure is injected through the AppHost's parameters** — the *same levers an operator uses in a demo*. A test that reached inside a service to make it fail would be testing a failure mode no operator can reproduce.

**The unit suite deliberately does not use EF InMemory.** It enforces neither row versions nor unique indexes. A green test against it would have *implied* the oversell guard and the double-charge guard worked while proving nothing of the sort. Those two guarantees are made by the database — so they are proven against the database, or they are not claimed.

That is the "zero fakes" rule of [ADR-005](architecture%20decision%20records/5.md) surviving contact with real test pressure, which is the strongest evidence for it.

---

## 11. What was deliberately left out

Recorded here so a reader knows each was a **choice**, not an oversight.

| Not built | Why |
|---|---|
| **A warehouse-management service** | [ADR-004](architecture%20decision%20records/4.md). Pick/pack internals add surface, not insight. Right-sizing is an architectural act. |
| **A transactional outbox** | [ADR-007](architecture%20decision%20records/7.md). The correct production answer; rejected here on **cost, not correctness**. The sweeper closes the same gap for one class, and the ADR says plainly that a system where that minute matters needs the outbox. |
| **Full event sourcing** | [ADR-003](architecture%20decision%20records/3.md). The event log earns its keep for orders. Imposing it on inventory rows — which want to be updated under contention — would be dogma, not design. |
| **A pricing/catalogue service** | [ADR-006](architecture%20decision%20records/6.md). Correct at scale; a sixth service whose only job is a lookup, here. The SKU price is already a property of the thing Inventory is holding. |
| **A price quote before checkout** | A real checkout quotes first, which needs a decision about what happens when the price moves between quote and reservation. Out of scope: the **failure paths** are the subject. |
| **Auth, real PII, a real processor, a real carrier** | It is a reference architecture, not a product. |
| **SignalR for status** | The customer view polls. One fewer moving part in a demo about a different thing. |

The ADRs also record where the code and the documents currently disagree — see the **Verification** sections. [ADR-001](architecture%20decision%20records/1.md), for instance, notes that while the Order *service* is the sole orchestrator, workflow knowledge now lives in three files inside it rather than one, because the recovery sweeper had to re-encode the state→command mapping. The saga is the sole *decider* but no longer the sole *knower*. That is a real leak, it is a maintenance hazard, and it is written down rather than tidied out of the record.

---

## 12. The path to production

What [ADR-005](architecture%20decision%20records/5.md) means by "going live is a config change" — and what it honestly does not.

**Genuinely a config change.** No service names a broker, a database, or a connection string. Every resource is resolved by Aspire connection name against the real Azure SDK. Swap `RunAsEmulator()` for a live account on the two `// TODO` markers in the AppHost, and the code is unchanged.

**Genuinely still work**, and named as such rather than waved through:

- **The emulators are not the cloud.** The Cosmos and Service Bus emulators have known behavioural gaps — throughput, throttling, feature coverage. A green local run does not prove production-readiness.
- **Cosmos RU provisioning and cost modelling.** Real, deferred, and not trivial.
- **Identity, networking, provisioning.** Managed identity instead of connection strings; private endpoints; infrastructure-as-code.
- **The outbox.** The moment delivery guarantees are the subject rather than compensation, the sweeper is no longer the right answer. [ADR-007](architecture%20decision%20records/7.md) says so itself.
- **Auth, real PII handling, a real processor and carrier.**

The [Delivery & Investment Plan](decks/Delivery_Investment_Plan.pdf) covers how that work phases out as an engagement.

---

## Where to start reading

If you have twenty minutes:

1. [`AppHost/Program.cs`](../src/OrderFlow.AppHost/Program.cs) — the wiring diagram of the entire system, commented as one.
2. [`OrderSaga.cs`](../src/OrderFlow.Orders.API/Managers/Saga/OrderSaga.cs) — the workflow, the compensations, and the ordering rule.
3. [`ServiceBusConsumer.cs`](../src/OrderFlow.ServiceDefaults/Messaging/ServiceBusConsumer.cs) — settlement, idempotency, and dead-lettering, in one place, once.
4. [`OrderSagaTests.cs`](../tests/OrderFlow.UnitTests/OrderSagaTests.cs) — the proof.
5. [ADR-001](architecture%20decision%20records/1.md) and [ADR-007](architecture%20decision%20records/7.md) — the reasoning, and an honest account of the one failure that nearly got away.
