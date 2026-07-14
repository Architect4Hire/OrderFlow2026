# Known Gaps

OrderFlow2026 is a reference build at roughly **90%** — fully scaffolded, mostly wired, and deliberately incomplete in a few places. This file names those places on purpose.

Two reasons it exists. First, honesty: a proof-of-concept that pretends to be finished is worse than one that tells you where its seams are. Second, it's a demonstration in itself — knowing precisely what is and isn't done, and why, is exactly the kind of judgment a client is hiring for. On a real engagement, the items below are where your specific requirements would land.

## Legend

- 🔴 **Stub** — interface/shape exists, implementation is a placeholder or `// TODO`.
- 🟡 **Partial** — works for the primary path; edges or hardening incomplete.
- 🟢 **Done** — complete for a proof-of-concept; production would still add scale/security review.

## Gaps

| Area                      | State | Notes                                                                                                                                            |
| ------------------------- | ----- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| Order saga — happy path   | 🟢    | Place → reserve → charge → dispatch → notify runs end to end.                                                                                    |
| Order saga — compensation | 🟡    | Compensating actions are in place on the main failure paths; a full failure-matrix pass (every step × every failure mode) is not yet exhaustive. |
| Idempotency / dedupe      | 🟡    | Payment-callback dedupe implemented; a uniform idempotency policy across *all* handlers is not yet applied everywhere.                           |
| Inventory concurrency     | 🟡    | Concurrency-correct reservation implemented; load-tested contention proof is pending.                                                            |
| Dead-lettering & retry    | 🟡    | Retry-with-backoff and dead-letter wired; alerting on the dead-letter queue is not.                                                              |
| Read-model projection     | 🟡    | Redis status projection works; convergence measurement (< 1s target) is not yet instrumented.                                                    |
| Observability / tracing   | 🟡    | App Insights + distributed tracing scaffolded; span coverage across every hop is incomplete.                                                     |
| Notification service      | 🔴    | Email/SMS is simulated; templates and delivery-failure handling are stubs.                                                                       |
| External integrations     | 🔴    | Payment and carrier are simulators behind interfaces (by design); real integrations are out of scope for the PoC.                                |
| Angular — operations view | 🟡    | Renders live read-model state; filtering, paging, and error states are minimal.                                                                  |
| Angular — customer view   | 🟡    | Order-status view works; auth and per-customer scoping are stubbed.                                                                              |
| AuthN / AuthZ             | 🔴    | No real identity yet; endpoints are open for local evaluation. First thing to add for anything real.                                             |
| Automated tests           | 🟡    | Saga and service unit tests exist; end-to-end failure-injection suite is partial.                                                                |
| CI/CD                     | 🔴    | Local-first via Aspire; no pipeline defined.                                                                                                     |
| Data migrations           | 🔴    | SQL schema is code-first for local runs; migration strategy is not established.                                                                  |

## What "the last 10%" would mean on a real engagement

The gaps above are not an accident of running out of time — they're the line between a build that *demonstrates the architecture* and a build that *carries a specific client's production requirements*. Closing them is engagement work: your auth model, your compliance regime, your scale targets, your integration partners. That's the Delivery phase in the [Delivery & Investment Plan](./decks/Delivery_Investment_Plan.pdf), and it's deliberately shaped by the client rather than guessed at here.

---

*Questions about scope, or about closing gaps like these on a system of your own: <robert@architect4hire.com>.*
