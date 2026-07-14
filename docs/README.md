# OrderFlow2026 — Documentation

Start here. This folder is the durable baseline for OrderFlow2026: the design, the decisions behind it, and the prompt library that generated the code. If you read it in order, the codebase in [`/src`](../src) will make sense in a way the code alone can't.

> **What OrderFlow2026 is.** A reference build — an event-driven order-fulfillment system designed and built solo with the [SCRUB framework](https://www.architect4hire.com/scrub), maintained in the open so prospective clients can see the work, not just hear about it. It is a proof-of-concept at roughly 90%: fully scaffolded, mostly wired, with a few deliberate gaps documented in [`KNOWN-GAPS.md`](./KNOWN-GAPS.md).

## Read in this order

1. **[High-Level Design](./design%20docs/high%20level%20design/Order%20Flow%20HLD.pdf)** — the system at a glance: the landscape, the saga lifecycle, the data stores, and the failure modes the design exists to address. Read this first.
2. **[Architecture Decision Records](./adr/README.md)** — *why* the system looks the way it does. Every significant choice, with its context and consequences, recorded so the design can't quietly drift from the code.
3. **[Delivery & Investment Plan](./decks/Delivery_Investment_Plan.pdf)** — how an engagement to build this is scoped, sequenced, and estimated: workstreams, phased roadmap, and the outcomes each phase is measured against.
4. **[SCRUB Prompt Walkthrough](./walkthroughs/scrub-inventory-reservation.md)** — a single prompt traced to the code it produced, so you can see how the prompt library functions as the system's source of truth.
5. **[Known Gaps](./KNOWN-GAPS.md)** — the ~10% deliberately left undone, named honestly.

## The system in three sentences

An Angular front end calls an **Order** service, which runs a **saga** that orchestrates four reacting services — **Inventory**, **Payment**, **Fulfillment**, and **Notification** — over a message bus. Cosmos holds the append-only event log, Redis backs the order-status read model, and SQL is the system-of-record for inventory and payments. Every failure path compensates, every handler is idempotent, and the whole thing runs on .NET Aspire against local emulators, so it boots on a laptop with no cloud spend.

## What lives where

| Path                   | Contents                                                           |
| ---------------------- | ------------------------------------------------------------------ |
| [`/docs`](.)           | This baseline — HLD, ADRs, delivery plan, walkthroughs, known gaps |
| [`/src`](../src)       | The scaffolded system the docs describe                            |
| [`/prompts`](prompts/prompts.md) | The SCRUB prompt library and AI-assisted-development assets        |

## A note on how this was built

OrderFlow2026 was produced with disciplined, AI-assisted development — the prompts that generated it are committed alongside the code and treated as first-class artifacts. When the prompts and the built system disagreed during development, the prompts were corrected to match reality, not the other way around. That is why the ADRs and the prompt library can be trusted as the source of truth: they are versioned, reviewed, and kept in sync on purpose.

---

*Maintained by Robert Felkins, principal architect at [Architect4Hire](https://www.architect4hire.com). Questions about the build, or about a fractional or contract engagement of your own: <robert@architect4hire.com>.*
