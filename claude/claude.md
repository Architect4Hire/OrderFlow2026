# OrderFlow Conventions (pre-loaded into every prompt)

## System (U)
OrderFlow is an event-driven order-fulfillment reference architecture POC. It
demonstrates .NET 10 + Aspire distributed services coordinated by a SAGA over an
asynchronous message bus, behind an Angular 20 frontend. Domain flow: order placed
→ inventory reserved → payment charged → fulfillment dispatched → customer notified,
with COMPENSATION on any failure. This is a POC — no real PII, no real payment
processor, no real carrier. It runs entirely on Aspire-local emulators (SQL Server,
Cosmos DB emulator, Redis, Service Bus emulator) but against real Azure SDKs so
going live is a config change, not a rewrite.

## Architecture (C)
- .NET 10, ASP.NET Core 10, C# 14, nullable + implicit usings on
- Aspire 13.3 for orchestration; emulated SQL Server, Cosmos DB, Redis, Service Bus
- Onion architecture per service: Controller/Consumer → Facade → Business → Data → DbContext
- Each service has a `Managers/` folder: DataContext/, Domain/, ViewModels/,
  ServiceModels/, Extensions/, Data/, Business/, Facades/, Consumers/
- Hand-rolled mapping extensions (no AutoMapper)
- EF Core 10, code-first, EnsureCreatedAsync for the POC (migrations later)
- ONE shared Contracts project holds message types (commands + events). Services
  reference Contracts ONLY — never each other.
- Angular 20 standalone components, signals, no NgModules

## Messaging (C)
- Azure Service Bus (emulated). Commands → one handler (queue). Events → many
  subscribers (topic/subscription).
- Every message carries a `CorrelationId` (the OrderId) and a `MessageId`.
- Consumers are idempotent: a durable processed-message key guards every handler.
- The Order service is the ONLY saga orchestrator. Other services react and reply;
  they never orchestrate.

## Naming (C)
- Interfaces prefixed `I` (e.g. `IInventoryFacade`)
- DbContext suffix on EF contexts (`InventoryDbContext`)
- ViewModel = incoming HTTP, ServiceModel = outgoing HTTP, Domain = internal
- Commands are imperative (`ReserveInventory`); events are past-tense (`InventoryReserved`)
- Extension methods grouped by entity (e.g. `OrderMappingExtensions`)

## Restrictions — apply to ALL prompts (R)
CRITICAL — Do NOT:
1. Use AutoMapper, Mapster, or any reflection-based mapper. Hand-rolled extensions only.
2. Put EF Core types or DbContext into a Controller, Consumer, Facade, or Business layer.
3. Return Domain entities from controllers/consumers — always map to ServiceModels/events.
4. Reference one service's project from another. They share ONLY the Contracts project.
5. Orchestrate the workflow anywhere but the Order saga. Reacting services reply; they
   do not call the next step themselves.
6. Consume a message without an idempotency guard. At-least-once delivery is assumed.
7. Hard-code connection strings — use Aspire's `Add…` resource references.
8. Introduce a distributed transaction or two-phase commit. Compensation only (ADR-001).

IMPORTANT — Do NOT:
9. Use synchronous EF or messaging calls. Everything async with CancellationToken.
10. Use `decimal` math without explicit rounding to 2 dp at boundaries.
11. Let a compensation path leave inventory reserved or a charge un-refunded.
12. Emit a message without CorrelationId + MessageId set.
13. Skip XML doc-comments on public Facade, Controller, or Consumer methods.

PREFERRED — Do NOT:
14. Introduce magic numbers — promote to `const` or appsettings.
15. Mix concerns in a single class — split if the file exceeds ~120 lines.
16. Add SignalR/WebSockets — the customer status view polls in this POC.