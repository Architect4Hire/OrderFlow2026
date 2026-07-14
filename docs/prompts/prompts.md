# OrderFlow — SCRUB Prompt Library

> A complete, ordered set of SCRUB-style prompts to recreate the OrderFlow POC from a blank repo, end-to-end. OrderFlow is an **event-driven** e-commerce order-fulfillment reference architecture: an order comes in, inventory is reserved, payment is charged, fulfillment is dispatched, and the customer is notified — coordinated by a **saga** with **compensation**, over an asynchronous message bus, all running on Aspire-local emulators with real Azure SDKs.

> **This library is derived from the OrderFlow High-Level Design.** Where the design doc says *what* and *why*, this library says *how* — as prompts. Section names below mirror the design doc so the two can be read side by side.

## How to use this library

Each prompt follows the **SCRUB framework** from the *Practical AI Assisted Development* book:

- **[S] Scope** — name the exact deliverable and methods. If it takes more than three sentences, split it.
- **[C] Constraints** — framework, architecture, naming, DI, messaging, testing. Match the codebase.
- **[R] Restrictions** — `Do NOT…` rules. Tiered: **CRITICAL / IMPORTANT / PREFERRED**.
- **[U] Usage** — who calls this, what the surrounding system is, the failure/observability context.
- **[B] Behavior** — what must NOT change (dominant element in Edit Mode).

Run the prompts in order. After each one, **verify against the original `[R]` restrictions** before continuing — this is the Verify step in the chain.

> **The one thing that makes OrderFlow different from a CRUD POC:** most of the value is in the *failure paths*, not the happy path. Idempotency, compensation, retries, and dead-lettering appear in nearly every `[R]` block below. That is not incidental — it is the point of the whole build, and it is what a reviewer reads the code to check. Do not let an agent "simplify" these away.

### Recommended tool mode per prompt

- **Agent Mode** (Copilot Agent or Claude Code) for prompts marked 🤖 — multi-file scaffolding.
- **Chat** for prompts marked 💬 — single-file or analytical work.
- **Edit Mode** for prompts marked ✏️ — narrow, surgical edits where `[B]` dominates.

---

## Phase 0 — Pre-load context (CLAUDE.md / copilot-instructions.md)

Before writing the first SCRUB prompt, drop this file at the repo root as `CLAUDE.md` (or `.github/copilot-instructions.md`). It pre-loads `C`, `U`, and most `R` so every later prompt can stay short.

```markdown
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
- EVERY consumer in EVERY service derives from `ServiceBusConsumer<TMessage>` in
  ServiceDefaults (A5). It owns the settlement rules — abandon-don't-complete,
  dead-letter poison, `(ConsumerName, MessageId)` — so no service can restate them
  slightly differently. Override `SubscriptionName` for a topic (null = queue) and
  `MaxConcurrentCalls` where concurrency is the behaviour under test.
- A message emitted in RESPONSE to another (a reply event, a compensation command)
  gets a deterministic MessageId — `MessagingConventions.DeterministicMessageId(
  correlationId, nameof(TheMessage))`. A random id makes the retry's re-emitted reply
  look brand new to the receiver's idempotency guard, and it is handled twice.
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
```

> With this loaded, every prompt below stays focused on `S`, `R`-deltas, and `B`. This is the **"Custom Instructions = Shorter, Safer Prompts"** pattern discussed in Practical AI-Assisted Development. Note how much of the pre-load is about *messaging discipline and failure behavior* — that is where an event-driven build goes wrong.

---

# PART A — Solution Scaffold, AppHost & Contracts

## Prompt A1 — Create the solution and `global.json` 💬

```text
[S] Create an empty .NET solution named `OrderFlow.slnx` and a `global.json` at
    the repo root pinning the SDK to 10.0.100 with rollForward latestFeature.

[C] .NET 10 LTS. No projects yet — just the .sln and global.json.

[R] Do NOT include any projects in the solution yet. We add them in later prompts.
    Do NOT add a Directory.Build.props yet.

[U] First step of a from-scratch POC build. Followed by per-project prompts.

[B] N/A — greenfield.
```

## Prompt A2 — Create the Contracts project (messages) 🤖

```text
[S] Create `src/OrderFlow.Contracts/OrderFlow.Contracts.csproj` (classlib, net10.0)
    plus one file per message group under `Messages/`:
    - `Messages/Commands` — records: ReserveInventory, ReleaseInventory,
      ChargePayment, RefundPayment, DispatchFulfillment. Create a class file per record
    - `Messages/Events` — records: OrderPlaced, InventoryReserved,
      InventoryRejected, PaymentSucceeded, PaymentDeclined, FulfillmentDispatched,
      FulfillmentFailed, OrderConfirmed, OrderFailed.  Create a class file per record
    - `Messages/MessageBase.cs` — an abstract record or interface carrying
      Guid MessageId, Guid CorrelationId (the OrderId), DateTime OccurredUtc.
    Every command and event derives from / implements the base.

[C] - Namespace `OrderFlow.Contracts.Messages`
    - Records, not classes. Init-only properties. Immutable.
    - Each message includes CorrelationId (OrderId) and MessageId.
    - Payloads are minimal: ids, amounts, SKUs, quantities, reasons — no domain
      entities, no EF types.

[R] CRITICAL — Do NOT:
    1. Reference any service project, EF Core, or Aspire from Contracts. This
       project is a leaf — everything references IT, it references nothing.
    2. Put behavior on the messages. They are DTOs only.
    IMPORTANT — Do NOT:
    3. Include a `Reason` string on success events — only on the *Rejected/
       Declined/Failed* ones.
    4. Version messages inline — if we need V2 later, add a new record. Leave a
       `// TODO: message versioning strategy` comment at the top of MessageBase.cs
       (there is no Events.cs — [S] mandates one file per record).

[U] The ONLY thing shared across services. Producers and consumers both bind to
    these types, so drift here is what breaks a distributed build. This is the
    contract in "Messaging & Contracts."

[B] N/A — new files.
```

## Prompt A3 — Create the Aspire AppHost project 🤖

```text
[S] Create `src/OrderFlow.AppHost/OrderFlow.AppHost.csproj`, `Program.cs`,
    `Properties/launchSettings.json`, and `appsettings.Development.json` that
    orchestrate: one SQL Server container (persistent volume) with two databases
    (InventoryDb, PaymentDb); a Cosmos DB emulator with one database (OrderEventsDb);
    a Redis container (order status read model); a Service Bus emulator with the
    queues/topics OrderFlow needs; five not-yet-existing API projects referenced as
    Projects.OrderFlow_Orders_API, _Inventory_API, _Payments_API, _Fulfillment_API,
    _Notification_API; and one Angular npm app at `../OrderFlow.Web` on port 4200.

[C] - Use `<Sdk Name="Aspire.AppHost.Sdk" Version="13.3.0" />` as an inline SDK
      element inside the .csproj (DO NOT use `<IsAspireHost>true</IsAspireHost>`
      — deprecated since Aspire 9.2; triggers NETSDK1206).
    - Target `net10.0`
    - PackageReferences: Aspire.Hosting.AppHost, Aspire.Hosting.SqlServer,
      Aspire.Hosting.Azure.CosmosDB, Aspire.Hosting.Redis,
      Aspire.Hosting.Azure.ServiceBus, Aspire.Hosting.JavaScript (all 13.3-aligned).
      (NOT Aspire.Hosting.NodeJs — it stops at 9.5.2 and has no 13.x release.)
    - SQL password via `builder.AddParameter("sql-password", secret: true)`.
    - SQL Server: `.WithDataVolume("orderflow-sql-data")
      .WithLifetime(ContainerLifetime.Persistent)`.
    - Cosmos: use the Cosmos DB EMULATOR (`.RunAsEmulator(...)`), NOT a live account.
      Declare the DATABASE **and the container**: `AddCosmosDatabase("OrderEventsDb")`
      then `.AddContainer("order-events", "/orderId")`. A database with no container is
      not a build error — it is a 404 at H2, which is a much worse place to find it.
      Point the Order API at the CONTAINER resource, not the database (Prompt B5).
    - Service Bus: use the Service Bus EMULATOR (`.RunAsEmulator(...)`). Declare the
      queues (per-command) and topics+subscriptions (per-event) the services need.
      OPEN: the `order-placed` topic currently has no subscriber AND no publisher —
      B7 appends OrderPlaced to the event store but never publishes it. Either have
      Business also `PublishEventAsync(orderPlaced)`, or drop the topic. Right now it is
      dead infrastructure.
    - Each API: `.WithReference(<its resources>).WaitFor(<its resources>)
      .WithExternalHttpEndpoints()`. Order API references Cosmos + Redis + Service Bus;
      Inventory + Payment reference SQL + Service Bus; Fulfillment + Notification
      reference Service Bus only.
    - Angular: `.AddJavaScriptApp("web", "../OrderFlow.Web", "start")
      .WithHttpEndpoint(port: 4200, env: "PORT")` with env vars pointing at the
      Order API (customer status) and the Order/Inventory/Payment APIs (ops view),
      then `.PublishAsDockerFile()`. (AddNpmApp is retired; AddJavaScriptApp takes
      the identical three arguments.)
    - `Properties/launchSettings.json` MUST exist with two profiles (http, https),
      each setting `applicationUrl` and the two Aspire dashboard endpoint env vars
      (ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL, ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL),
      with distinct ports across profiles. Without this file, `dotnet run`
      returns immediately with no dashboard — a silent no-op.
    - `appsettings.Development.json` may include a `Parameters:sql-password` fallback
      so first-time `dotnet run` succeeds without a manual `dotnet user-secrets set`.

[R] CRITICAL — Do NOT:
    1. Hard-code the SQL password — use AddParameter with secret:true (a dev-only
       fallback in appsettings.Development.json is acceptable).
    2. Point Cosmos or Service Bus at a LIVE Azure resource. Emulators only — the
       whole POC is zero-spend (ADR-005). Leave a `// TODO: swap RunAsEmulator for
       live account via config` comment at each.
    3. Bake API URLs into Angular — pass them via environment variables.
    4. Use `<IsAspireHost>true</IsAspireHost>`. Use the `<Sdk .../>` element.
    5. Skip `Properties/launchSettings.json`.
    IMPORTANT — Do NOT:
    6. Add ProjectReferences yet to the five APIs in the csproj — leave them as
       TODO comments; we create the projects later and come back (Prompt H1).
    7. Give every service a reference to every resource. Each service gets ONLY the
       resources it uses (see [C]) — least privilege is an architectural signal.
    PREFERRED — Do NOT:
    8. Add an OpenTelemetry collector container — ServiceDefaults + the dashboard
       cover telemetry for this POC.

[U] The orchestrator a developer runs with `dotnet run`. The Aspire dashboard is the
    front door to logs, traces, resource health, AND the live distributed trace that
    is the centerpiece of the demo (see "Observability").

[B] N/A — new files.
```

## Prompt A4 — Create the ServiceDefaults project 🤖

```text
[S] Create `src/OrderFlow.ServiceDefaults/OrderFlow.ServiceDefaults.csproj` and
    `Extensions.cs` exposing `AddServiceDefaults()` on IHostApplicationBuilder and
    `MapDefaultEndpoints()` on WebApplication. Inside AddServiceDefaults, wire up:
    OpenTelemetry (metrics + tracing + logging), default health checks (liveness tag
    "live"), service discovery, and standard HTTP client resilience.

[C] - `IsAspireSharedProject=true`, net10.0
    - PackageReferences: Microsoft.Extensions.ServiceDiscovery,
      Microsoft.Extensions.Http.Resilience, OpenTelemetry.Exporter.OpenTelemetryProtocol,
      OpenTelemetry.Extensions.Hosting, OpenTelemetry.Instrumentation.AspNetCore,
      OpenTelemetry.Instrumentation.Http, OpenTelemetry.Instrumentation.Runtime
      (all versions aligned to the .NET 10 / Aspire 13.3 wave).
    - Add tracing instrumentation for the messaging client so message spans join the
      same distributed trace as HTTP spans (this is what makes one order traceable
      end-to-end).
    - FrameworkReference Microsoft.AspNetCore.App
    - Namespace `Microsoft.Extensions.Hosting`

[R] Do NOT:
    1. Add an OTLP exporter unless OTEL_EXPORTER_OTLP_ENDPOINT is set in config.
    2. Expose health endpoints in non-Development environments.

[U] Imported by every API so they share telemetry, health, and HTTP-client policies.
    The messaging-trace wiring here is load-bearing for the observability demo.

[B] N/A — new files.
```

## Prompt A5 — Shared messaging abstraction 🤖

```text
[S] Create `src/OrderFlow.ServiceDefaults/Messaging/` with:
    - `IMessageBus.cs` — `SendCommandAsync<T>(T cmd, CancellationToken)` and
      `PublishEventAsync<T>(T evt, CancellationToken)`.
    - `ServiceBusMessageBus.cs` — implements IMessageBus over the Azure Service Bus
      SDK, stamping MessageId (if unset) and propagating CorrelationId + the current
      trace context onto every outgoing message.
    - `IdempotencyKeyStore.cs` — interface + a simple implementation recording
      processed (ConsumerName, MessageId) pairs so a redelivered message is a no-op.
    - `MessagingConventions.cs` — `EntityNameFor<T>()` (kebab-case of the type name)
      and `DeterministicMessageId(Guid correlationId, string discriminator)`
      (SHA-256 of `"{correlationId:N}:{discriminator}"`, first 16 bytes).
    - `ServiceBusConsumer.cs` — the abstract base EVERY consumer in EVERY service
      derives from. Subclass supplies `HandleAsync(IServiceProvider scope, TMessage,
      ct)`; the base owns processor setup, W3C trace re-parenting, the idempotency
      guard, and settlement. Virtuals: `SubscriptionName` (null = queue, a name = topic
      subscription) and `MaxConcurrentCalls` (default 1).
    - `MessagingExtensions.cs` — `AddOrderFlowMessaging(this IHostApplicationBuilder)`
      registering the bus and the idempotency store.

[C] - Use the Azure.Messaging.ServiceBus SDK (works against the emulator unchanged).
    - The consumer base is SHARED, not per-service. Three services consume commands off
      queues and two consume events off topics; forked bases mean four near-identical
      copies of the settlement rules, which is the last logic in this system you want
      drifting. One class, one `SubscriptionName` virtual, covers both shapes.
    - The base's ActivitySource must be named for the ENTRY ASSEMBLY, which is the
      ApplicationName ServiceDefaults passes to `AddSource(...)`. Name it anything else
      and the spans are silently dropped — every consumer hop vanishes from the
      end-to-end trace that is supposed to prove the saga works.
    - Add PackageReference Aspire.Azure.Messaging.ServiceBus (13.3-aligned) and
      register the client with `builder.AddAzureServiceBusClient("servicebus")` so the
      connection string comes from the AppHost resource reference, never from config.
    - Add a ProjectReference from ServiceDefaults to ../OrderFlow.Contracts — the bus
      is generic over MessageBase so it can read CorrelationId/MessageId. Contracts
      stays a leaf; nothing flows the other way.
    - Resolve the queue/topic name from the message type: kebab-case of the type name
      (ReserveInventory → `reserve-inventory`). That convention is the contract between
      Contracts and the entities the AppHost declares.
    - The idempotency store may be backed by the service's own SQL/Cosmos context for
      the POC; leave a `// TODO: shared durable store` comment.
    - Trace context propagation uses the standard W3C traceparent on the message
      ApplicationProperties.
    - MessageBase is immutable and `with` is illegal on a type parameter, so stamp
      MessageId by patching the serialized JsonObject and mirror it onto the
      ServiceBusMessage envelope (consumers dedupe from the envelope).

[R] CRITICAL — Do NOT:
    1. Swallow send/publish exceptions. A failed publish must surface so the saga can
       react — silent message loss is the worst failure mode in this system.
    2. Generate a NEW CorrelationId inside SendCommand/PublishEvent. Correlation flows
       from the originating OrderPlaced and never changes for that order.
    IMPORTANT — Do NOT:
    3. Make the idempotency check optional per-call. Every consumer uses it — enforced
       in `ServiceBusConsumer<T>` above, which is why the base lives HERE and not in
       each service.
    4. Complete/settle a message anywhere but in the base, after HandleAsync returns.
       This is what lets a consumer publish its reply inside the handler and rely on a
       failed publish redelivering the whole command.

[U] The seam every service publishes and consumes through. Idempotency, correlation and
    settlement live here so no individual consumer can forget them.

[B] N/A — new files.
```

---

# PART B — Order service (saga host — the template)

> The Order service owns the workflow. It is the reference implementation for the
> onion layering AND the home of the saga. Once Order is complete, the reacting
> services (Inventory, Payment, Fulfillment, Notification) follow the same layered
> pattern with their own domain. This is the **layered SCRUB** pattern from the book.

## Prompt B1 — Order API csproj + folder skeleton 🤖

```text
[S] Create `src/OrderFlow.Orders.API/OrderFlow.Orders.API.csproj` as a
    Microsoft.NET.Sdk.Web project, plus the empty `Managers/` tree: DataContext/,
    Domain/, ViewModels/, ServiceModels/, Extensions/, Data/, Business/, Facades/,
    Consumers/, Saga/, and a top-level Controllers/ folder. Also add a placeholder
    `Program.cs` (see [R]3).

[C] - Target net10.0, nullable+ImplicitUsings on, RootNamespace OrderFlow.Orders.API
    - PLURAL namespace is deliberate. `OrderFlow.Order.API` + the `Order` entity of
      Prompt B2 is a CS0118 collision: inside any sibling namespace the identifier
      `Order` binds to the enclosing NAMESPACE, not the class, and a normally-placed
      `using` cannot win. Same trap for Payment → use OrderFlow.Payments.API.
      Inventory/Fulfillment/Notification have no colliding entity and stay singular.
    - PackageReferences, every service: Azure.Messaging.ServiceBus,
      Scalar.AspNetCore, Microsoft.AspNetCore.OpenApi, Microsoft.OpenApi (>= 2.7.5)
        · Scalar RENDERS an OpenAPI document; it does not PRODUCE one. In .NET 10 the
          generator ships as a package, not in the shared framework, so Scalar without
          Microsoft.AspNetCore.OpenApi + AddOpenApi()/MapOpenApi() is a blank page.
        · Microsoft.OpenApi must be pinned EXPLICITLY. Microsoft.AspNetCore.OpenApi
          10.0.9 drags in 2.0.0 transitively, which carries GHSA-v5pm-xwqc-g5wc (high:
          circular schema references terminate OpenAPI parsing), patched in 2.7.5.
          Without the pin the build raises NU1903.
    - PackageReferences, per service (least privilege — only what it actually uses):
        · Orders:    Aspire.Microsoft.Azure.Cosmos, Aspire.StackExchange.Redis
        · Inventory: Aspire.Microsoft.EntityFrameworkCore.SqlServer
        · Payments:  Aspire.Microsoft.EntityFrameworkCore.SqlServer
        · Fulfillment / Notification: neither — bus only
    - ProjectReference to ../OrderFlow.ServiceDefaults AND ../OrderFlow.Contracts

[R] CRITICAL — Do NOT:
    1. Add a Domain folder outside `Managers/Domain/`. Exactly ONE per service.
    2. ProjectReference any other service. Contracts + ServiceDefaults only.
    IMPORTANT — Do NOT:
    3. Create any code files beyond a placeholder `Program.cs` — csproj and empty
       directories otherwise. The Web SDK needs an entry point or the whole solution
       fails CS5001 ("no static 'Main'") for the ten prompts until B11 lands, which
       costs you `dotnet build` as the green/red gate on every one of them. Stub it
       as `WebApplication.CreateBuilder(args).Build().Run();` with a TODO naming B11,
       which replaces it wholesale.

[U] Skeleton step before domain, models, layers, saga, consumers, controller.
    Reuse this prompt verbatim for the other four services — the library has no
    separate csproj/skeleton prompt for Inventory, Payments, Fulfillment, or
    Notification, so C1/D1/E1/F1 all assume a project that this step creates.

[B] N/A.
```

## Prompt B2 — Order domain (Order + OrderLine + OrderState enum) 💬

```text
[S] Create `Managers/Domain/Order.cs` with:
    - enum OrderState { Placed=0, Reserved=1, Paid=2, Dispatched=3, Confirmed=4,
      Failed=9 }
    - class Order: Id (Guid), CustomerRef (string), State (OrderState), Subtotal,
      Total (decimal), FailureReason (string), CreatedUtc, UpdatedUtc,
      List<OrderLine> Lines.
    - class OrderLine: Id, OrderId, Sku (string), Quantity (int), UnitPrice (decimal).

[C] - Namespace `OrderFlow.Orders.API.Managers.Domain` (plural — see B1 [C])
    - All string properties initialized to `string.Empty`
    - XML doc-comment on Order noting it is the saga aggregate root

[R] CRITICAL — Do NOT:
    1. Add EF/Cosmos attributes — persistence config lives in the DbContext.
    2. Add validation attributes — those live on ViewModels.
    IMPORTANT — Do NOT:
    3. Put saga transition logic on the entity. State changes go through the saga
       (Prompt B8) so every transition is one auditable place.

[U] Internal aggregate. The Order is the saga root; its State is the saga's state.

[B] N/A.
```

## Prompt B3 — Order ViewModel + ServiceModel 💬

```text
[S] Create:
    - `Managers/ViewModels/PlaceOrderViewModel.cs` — CustomerRef (Required) and a
      List<OrderLineViewModel> Lines (Required, MinLength 1); OrderLineViewModel has
      Sku (Required), Quantity (Range 1..100), UnitPrice (Range 0.0..99999.99).
    - `Managers/ServiceModels/OrderServiceModel.cs` — Id, CustomerRef, State (string),
      Subtotal, Total, FailureReason, Lines (as service-model lines: Id, Sku, Quantity,
      UnitPrice, and a derived LineTotal), timestamps.

[C] - Money Ranges use the DECIMAL overload:
      `[Range(typeof(decimal), "0.0", "99999.99", ParseLimitsInInvariantCulture = true)]`.
      The (double, double) overload round-trips money through binary floating point and
      can misjudge exactly the boundary value 99999.99.

[R] CRITICAL — Do NOT:
    1. Accept State, Id, Subtotal, or Total on the incoming ViewModel — all server-
       controlled. The client sends lines; the server prices and drives state.
       OPEN TENSION: [S] nonetheless puts UnitPrice on OrderLineViewModel, so the client
       DOES price the order — and therefore controls Total, and therefore the amount
       ChargePayment authorizes. Implemented as [S] says, with a `// TODO:` at the
       property. Resolve by either (a) pricing from the SKU server-side (needs a price
       source — StockItem has none), or (b) recording it as an explicit POC
       simplification in an ADR. Do not leave it merely implicit.
    IMPORTANT — Do NOT:
    2. Reference the Domain model from either file.

[U] ViewModel binds from the customer "place order" call. ServiceModel is what the
    status view polls.

[B] N/A.
```

## Prompt B4 — Order mapping extensions 💬

```text
[S] Create `Managers/Extensions/OrderMappingExtensions.cs`:
    - ToDomain(this PlaceOrderViewModel) → Order (new Guid for order and each line,
      State=Placed, computes Subtotal and Total rounded to 2 dp, UTC timestamps).
    - ToServiceModel(this Order) → OrderServiceModel (State via .ToString()).
    - ToServiceModels(this IEnumerable<Order>).
    - ToMessageLines(this IEnumerable<Domain.OrderLine>) → IReadOnlyList<Contracts
      OrderLine>. ReserveInventory and DispatchFulfillment need it, and mapping belongs
      here rather than as a private copy in Business (Prompt B7).

[C] - String copies use `.Trim()`; decimals via `decimal.Round(x, 2)`; timestamps
      from `DateTime.UtcNow`. Read the clock ONCE per aggregate so a freshly placed
      order has CreatedUtc == UpdatedUtc exactly, not microseconds apart.
    - Domain.OrderLine and Contracts.OrderLine share a name deliberately (persisted vs
      on the wire). This file is the only place both are in scope, so alias one:
      `using ContractLine = OrderFlow.Contracts.Messages.OrderLine;`
    - For the POC, Total == Subtotal (no tax/shipping here — that is not what this
      demo proves). Leave a `// TODO: pricing engine` comment.
    - Rounding, two known sharp edges — decide, don't inherit:
      (a) `decimal.Round(x, 2)` is BANKER'S rounding (MidpointRounding.ToEven), so
          2.005 → 2.00. Correct for accounting, surprising for retail.
      (b) Subtotal is round(Σ qty×price) while each LineTotal is round(qty×price).
          These can disagree if a client posts a UnitPrice with >2 decimal places
          (Range bounds magnitude, not scale), so the status view can show lines that
          visibly don't add up. Fix by summing the ROUNDED line totals, or by
          constraining UnitPrice to 2dp on the ViewModel.

[R] CRITICAL — Do NOT:
    1. Use AutoMapper/Mapster/reflection. Hand-rolled assignments only.
    2. Mutate the input ViewModel.
    IMPORTANT — Do NOT:
    3. Set State to anything but Placed on ToDomain — the saga owns every later state.

[U] Used by the Business layer when a customer places an order.

[B] N/A.
```

## Prompt B5 — Order event store (Cosmos, append-only) 🤖

```text
[S] Create `Managers/DataContext/OrderEventStore.cs` exposing IOrderEventStore with:
    AppendAsync(Guid orderId, object domainEvent, CancellationToken),
    ReadStreamAsync(Guid orderId, CancellationToken) → ordered event list,
    and a lightweight envelope type (EventId, OrderId, Type, OccurredUtc, Payload).
    Back it with the Cosmos DB SDK, partitioned by OrderId.

[C] - Append-only. Events are never updated or deleted.
    - Partition key = OrderId so one order's stream reads from a single partition
      (this is the ADR-002 justification made real).
    - Use the Aspire-registered Cosmos client: `builder.AddAzureCosmosContainer(
      "order-events", configureClientOptions: ...)` resolves a `Container` straight
      into DI from the AppHost resource — no database/container name strings in the
      data layer, no connection string anywhere.
    - The AppHost must DECLARE the container (A3 only declared the database):
      `orderEventsDb.AddContainer("order-events", "/orderId")`, and the Order API
      references the CONTAINER, not the database.
    - LOAD-BEARING: set `options.UseSystemTextJsonSerializerWithOptions =
      new JsonSerializerOptions(JsonSerializerDefaults.Web)`. The partition key path is
      "/orderId" (camelCase), but the Cosmos SDK's default serializer writes properties
      as declared — "OrderId" — and EVERY write fails on a partition-key mismatch.
      Bundle this into an `AddOrderEventStore(this IHostApplicationBuilder)` extension
      so B11 cannot forget it or "tidy away" the options lambda.
    - Cosmos requires the document key to be named `id`: `[JsonPropertyName("id")]` on
      EventId.

[R] CRITICAL — Do NOT:
    1. Update or delete an existing event. Ever. Append is the only write. Use
       `CreateItemAsync`, NEVER `UpsertItemAsync` — upsert is the more convenient call,
       it looks harmless, and it silently overwrites, turning the audit trail into a
       mutable record. A colliding id must throw.
    2. Query across partitions on the hot path — status reads come from Redis, not a
       cross-partition Cosmos scan. Pin `QueryRequestOptions.PartitionKey`; omit it and
       Cosmos silently fans out across every partition, which still WORKS, which is what
       makes it dangerous.
    IMPORTANT — Do NOT:
    3. Store the mutable Order aggregate here. This is the EVENT log; the aggregate's
       current state is projected into the read model (Prompt B6).
    4. Claim a total order from OccurredUtc alone. Two events appended in the same clock
       tick tie, and Cosmos cannot break the tie on a second field without a composite
       index. Leave a `// TODO:` — a real event store carries a monotonic per-stream
       sequence number, and adding it after the saga is writing is expensive.

[U] The system of record for what happened to an order. The saga appends to it on
    every transition; it is the audit trail a client sees in a trace.

[B] N/A.
```

## Prompt B6 — Order status read model (Redis) 🤖

```text
[S] Create `Managers/Data/OrderReadModel.cs` exposing IOrderReadModel with:
    SetStatusAsync(OrderServiceModel, CancellationToken),
    GetStatusAsync(Guid orderId, CancellationToken) → OrderServiceModel?,
    ListActiveAsync(CancellationToken) → the not-yet-terminal orders (for ops).
    Back it with the Aspire-registered Redis connection.

[C] - `builder.AddRedisClient("redis")` registers IConnectionMultiplexer. Wrap it in an
      `AddOrderReadModel(this IHostApplicationBuilder)` extension for B11.
    - Key per order (e.g. `order:{id}`); serialize the OrderServiceModel as JSON.
    - "Active" = State not in { Confirmed, Failed }. Maintain a set for cheap listing.
    - Write the document and the active-set membership in ONE MULTI/EXEC
      (`db.CreateTransaction()`). As two separate calls, a failure between them strands a
      Confirmed order in the ops "in flight" list forever, or hides an in-flight order
      from ops — the projection-drifts-from-reality bug that makes people stop trusting
      a read model and start reading the event log on every poll, which is precisely what
      ADR-003 exists to avoid.
    - Derive "terminal" by `Enum.TryParse<OrderState>`, not by string comparison, so
      renaming a state is a compile error here rather than an order that silently never
      leaves the ops list.
    - ListActive: one SMEMBERS then ONE MGET, not N round trips — the ops view polls it.
      Skip set members whose document is missing rather than throwing; a drifted set
      should degrade one row, not the whole view.
    - GOTCHA: RedisValue converts implicitly to BOTH string and ReadOnlySpan<byte>, so
      `JsonSerializer.Deserialize(payload)` and `Guid.Parse(id)` are ambiguous (CS0121).
      Cast explicitly: `(string)payload!`.

[R] CRITICAL — Do NOT:
    1. Treat Redis as the system of record. It is a projection/cache — it can be
       rebuilt from the Cosmos event stream. Leave a `// TODO: rebuild-from-stream`
       comment. Flushing Redis must leave the system degraded, never WRONG: the ops list
       goes empty until new orders arrive, and not one order is lost.
    IMPORTANT — Do NOT:
    2. Compute status here. The saga sets it; this store only persists/serves it. The
       single judgement it may make is classifying terminal vs active, per [C].

[U] Serves the customer status view and the ops list without touching the event log
    each poll. This is the "light CQRS read model" from Data & Persistence (ADR-003).

[B] N/A.
```

## Prompt B7 — Order Business + Facade 🤖

```text
[S] Create `Managers/Business/OrderBusinessManager.cs` (IOrderBusinessManager) and
    `Managers/Facades/OrderFacade.cs` (IOrderFacade).
    - Business.PlaceAsync(PlaceOrderViewModel): map to domain, append OrderPlaced to
      the event store, write the initial status to the read model, START the saga by
      publishing/sending the first command, return the ServiceModel.
    - Business.GetStatusAsync(Guid) → read model.
    - Business.ListActiveAsync() → read model.
    - Facade methods mirror these, one-liners delegating to Business.

[C] - Business depends on IOrderEventStore, IOrderReadModel and IMessageBus — NOT on
      EF/Cosmos/Redis SDK types directly. (This line USED to say "and the saga entry
      point". There isn't one: B8's IOrderSaga is six On* reaction methods and no Start.
      See [R]2.)
    - Facade depends on IOrderBusinessManager only.
    - CorrelationId IS the OrderId, set once in PlaceAsync and never regenerated. Leave
      MessageId unset on bus-bound messages — the bus stamps it (A5).
    - Order of operations is APPEND → PROJECT → SEND, deliberately. A failed append means
      nothing was sent and the order simply does not exist. The reverse would leave the
      saga running against an order with no record of it — strictly worse.

[R] CRITICAL — Do NOT:
    1. Reference Cosmos/Redis/ServiceBus SDK types from the Facade.
    2. Drive later saga steps from Business. Business only STARTS the saga (sends the
       first command, ReserveInventory). Everything after is consumer-driven (B9).
       OPEN TENSION: this means "the first step of a placed order is to reserve
       inventory" lives in Business — the one piece of orchestration outside the saga,
       and exactly what diagnostic I6 is written to catch. To close it, add a StartAsync
       bullet to B8's [S] and have Business call that instead (~6 lines). Recommended:
       B8 is the file a reviewer opens first.
    IMPORTANT — Do NOT:
    3. Return Domain entities from any method.
    4. Pretend the three writes are atomic. Append (Cosmos) → project (Redis) → send
       (Service Bus) has no transaction across it: if the send throws, the order sits at
       Placed with no saga running. Leave a `// TODO: outbox`. Note this stuck order is
       DEMONSTRABLE, not theoretical — the ops view has a stuck-orders panel and H3 is a
       failure-injection walkthrough, so it may be a feature rather than a bug.

[U] The Facade is the only thing the Controller depends on. Business is where "place
    an order" is coordinated.

[B] N/A.
```

## Prompt B8 — The saga (state machine + compensation) 🤖

```text
[S] Create `Managers/Saga/OrderSaga.cs` (IOrderSaga) — the heart of the system.
    It advances an order through its states in response to events and issues the next
    command or a compensation. Methods (each takes the event + CancellationToken):
    - OnInventoryReserved → charge payment (append InventoryReserved, transition
      Placed→Reserved, send ChargePayment).
    - OnInventoryRejected → fail immediately (transition →Failed with reason; no
      compensation needed).
    - OnPaymentSucceeded → dispatch fulfillment (→Paid, send DispatchFulfillment).
    - OnPaymentDeclined → COMPENSATE: send ReleaseInventory, →Failed with reason.
    - OnFulfillmentDispatched → confirm (→Dispatched then →Confirmed, publish
      OrderConfirmed).
    - OnFulfillmentFailed → COMPENSATE: send RefundPayment, send ReleaseInventory,
      →Failed with reason.
    Every transition appends to the event store AND updates the read model.

[C] - The saga reads current state from the event store (or a projected aggregate),
      not from an in-memory field, so it survives restarts.
    - Transitions are guarded: an event for an order already in a terminal state is a
      no-op (idempotent — a redelivered event must not re-fire compensation).
    - ONLY OrderConfirmed and OrderFailed are terminal. InventoryRejected,
      PaymentDeclined and FulfillmentFailed are the CAUSE, not the conclusion: they
      record a reason and leave the order non-terminal until the compensations are away.
    - Send every outbound message BEFORE recording the terminal state. The terminal
      guard is what makes a redelivery a no-op — record Failed first and a retry of the
      same event is guarded out before it can re-send the compensation, which strands
      the stock the guard was supposed to protect.
    - Every emitted message gets `MessagingConventions.DeterministicMessageId(orderId,
      nameof(TheMessage))` so a replay is deduped by the receiver's (ConsumerName,
      MessageId) guard rather than issuing a second refund.
    - All state changes for an order funnel through this class — the ONE auditable
      place transitions happen.

[R] CRITICAL — Do NOT:
    1. Introduce a distributed transaction or 2PC. Compensation only (ADR-001).
    2. Leave inventory reserved on ANY failure path. PaymentDeclined and
       FulfillmentFailed BOTH must release the reservation. This is the single most
       reviewed behavior in the whole build — get it exactly right.
    3. Re-run a compensation if the triggering event is redelivered. Terminal-state
       guard makes replays safe.
    IMPORTANT — Do NOT:
    4. Advance more than one step per event. The saga reacts to one event, issues one
       next action, and returns. No look-ahead orchestration.
    5. Swallow a failure to send a compensation command — a lost ReleaseInventory is
       exactly the silent-stock-loss bug this system exists to prevent.

[U] The compensation handler is the file a knowledgeable reviewer reads first to
    decide whether the author has actually operated one of these systems. Treat it
    as the centerpiece deliverable.

[B] N/A.
```

## Prompt B9 — Order consumers (react to reply events) 🤖

```text
[S] Create `Managers/Consumers/` with one consumer per inbound event the Order
    service subscribes to: InventoryReserved, InventoryRejected, PaymentSucceeded,
    PaymentDeclined, FulfillmentDispatched, FulfillmentFailed. Each consumer:
    checks the idempotency store, then delegates to the matching IOrderSaga method.

[C] - Each consumer derives from `ServiceBusConsumer<TEvent>` (A5) and overrides
      `SubscriptionName => "order-saga"` — these six read TOPIC SUBSCRIPTIONS, not
      queues. The base already owns the idempotency guard, settlement and tracing, so
      each subclass is one expression-bodied HandleAsync delegating to the saga. If one
      of them ever grows an `if`, the layering has broken.
    - Leave `MaxConcurrentCalls` at the default of 1. The saga mutates one order's
      stream per event and there is nothing to gain from racing it (contrast C3, where
      concurrency IS the behaviour under test).
    - PENDING: B9 was built before the shared base existed, so the code still carries a
      local `OrderSagaConsumer<T>` that duplicates it. Folding it onto
      `ServiceBusConsumer<T>` is a one-line change (`SubscriptionName => "order-saga"`)
      and deletes the copy. Until then, two near-identical settlement implementations
      exist and can drift.
    - Register consumers as hosted background processors over the Service Bus
      subscriptions declared in the AppHost.

[R] CRITICAL — Do NOT:
    1. Put saga/transition logic in a consumer. It guards idempotency and delegates —
       nothing else.
    2. Ack/complete a message before the saga call succeeds. On handler failure, let
       the message retry (and eventually dead-letter) rather than dropping it.
    IMPORTANT — Do NOT:
    3. Share one idempotency key across different consumers — the key is
       (ConsumerName, MessageId).

[U] These are how the saga hears back from the reacting services. The idempotency
    guard here is what makes "duplicate payment callback" a no-op (Failure matrix).

[B] N/A.
```

## Prompt B10 — Order controller 🤖

```text
[S] Create `Controllers/OrdersController.cs`:
    POST /api/Orders                → place an order (201 CreatedAtRoute, returns
                                       OrderServiceModel with State=Placed).
    GET  /api/Orders/{id:guid}      → current status (404 if unknown).
    GET  /api/Orders/active         → ops list of non-terminal orders.

[C] - [ApiController], [Route("api/[controller]")], [Produces("application/json")]
    - OrdersController, PLURAL — `[controller]` strips only the "Controller" suffix, so
      OrderController would route to /api/Order and every client path would be wrong.
    - 201 via `CreatedAtRoute` with a named route, NOT `CreatedAtAction(nameof(...))`:
      MVC strips the "Async" suffix from action names, so nameof(GetStatusAsync) looks
      for an action that does not exist and throws at RUNTIME, not compile time.
    - Inject IOrderFacade via primary constructor
    - ProducesResponseType attributes for Swagger

[R] CRITICAL — Do NOT:
    1. Inject the event store, read model, saga, or bus — only IOrderFacade.
    2. Return Domain entities — only OrderServiceModel.
    IMPORTANT — Do NOT:
    3. Block on the saga completing. POST returns as soon as the order is Placed and
       the first command is sent — the rest happens asynchronously and the client
       polls GET for progress.

[U] Called by the Angular customer view (place + poll) and ops view (active list).

[B] N/A.
```

## Prompt B11 — Order Program.cs wiring 🤖

```text
[S] REPLACE the placeholder `Program.cs` from B1 wholesale. Order API wiring:
    AddServiceDefaults, `AddOrderEventStore()` (B5), `AddOrderReadModel()` (B6),
    `AddOrderFlowMessaging()` (A5), DI for Business → Facade and the saga (Scoped),
    the hosted consumers, Controllers, Scalar, and a CORS policy "WebCors" for
    localhost:4200. Enable the API reference UI in Development. Register the Service
    Bus subscriptions/queues this service listens on.

[C] - Call the AddOrderEventStore / AddOrderReadModel / AddOrderFlowMessaging
      extensions rather than open-coding AddAzureCosmosContainer / AddRedisClient /
      AddAzureServiceBusClient. Each one carries a setting that is silently fatal if
      dropped — most of all the Cosmos camelCase serializer (B5 [C]), without which
      every append fails on a partition-key mismatch.
    - The API-reference UI is Scalar (`Scalar.AspNetCore`, per B1), not Swashbuckle.
      It needs `AddOpenApi()` + `MapOpenApi()` beside `MapScalarApiReference()` —
      Scalar renders a document it does not generate. Without them: a blank page.
    - CORS origin from configuration (`WebOrigin`), defaulting to
      http://localhost:4200. `AllowAnyOrigin` is not merely against [R]1 — combined
      with AllowCredentials it is illegal and throws at startup.
    - The registration ORDER is [R]2: the three infrastructure extensions, THEN
      AddOrderConsumers(). The consumers are hosted services that start pulling the
      moment the host starts.

[R] CRITICAL — Do NOT:
    1. Allow CORS from `*` — only the web origin.
    2. Start consuming before the read model/event store connections are registered.
    IMPORTANT — Do NOT:
    3. Expose the API-reference UI in non-Development.

[U] Entrypoint Aspire executes. Resource connection names MUST match those declared
    in the AppHost: the Cosmos CONTAINER `order-events` (not the OrderEventsDb
    database — see B5), the Redis resource `redis`, the Service Bus resource
    `servicebus`.

[B] N/A.
```

---

# PART C — Inventory service (concurrency-correct reservation)

> Same onion layering as Order (B1→B11 minus the saga/read-model), different domain.
> Below are the **deltas**. Inventory's whole reason to exist is proving no-oversell
> under contention.
>
> **Run Prompt B1 first**, substituting `OrderFlow.Inventory.API` (singular — no
> `Inventory` entity, so no CS0118) and Inventory's package list. C1 assumes the
> project already exists; there is no separate skeleton prompt for it.
>
> **Open question, decide before C1.** `ReservationState` is `{ Held, Released }` and
> the contracts have `ReserveInventory` / `ReleaseInventory` — but NOTHING tells
> Inventory the goods shipped. On the happy path the hold stays `Held` forever and
> `OnHand` is never decremented. The arithmetic survives (`Available = OnHand -
> Reserved` is unchanged either way), but the ops view fills with `Held` rows that
> shipped weeks ago sitting next to `Held` rows stranded by a lost compensation — the
> same colour on screen, and telling them apart is the entire diagnostic this POC
> exists to demonstrate. Either add `Consumed = 2` plus a `CommitInventory` command
> (A2 contract + ~4 lines in B8's OnFulfillmentDispatched, alongside the confirm — same
> shape as OnFulfillmentFailed already sending two commands), or record it as an
> explicit POC simplification in an ADR. Do not leave it implicit.

## Prompt C1 — Inventory domain + models 💬

```text
[S] Create:
    - `Managers/Domain/StockItem.cs` — Sku (string, key), OnHand (int), Reserved
      (int), a computed Available => OnHand - Reserved, RowVersion (byte[] — the
      optimistic-concurrency token), UpdatedUtc.
    - `Managers/Domain/Reservation.cs` — Id, OrderId, Sku, Quantity, State
      { Held=0, Released=1 }.
    - ServiceModels exposing SKU availability for the ops view.

[C] - Namespace `OrderFlow.Inventory.API.Managers.Domain`
    - RowVersion is the EF concurrency token (`[Timestamp]`-equivalent configured in
      OnModelCreating, NOT via attribute — config lives in the context).
    - Sku is the natural key. No surrogate id: the bus talks in SKUs, so an int id
      would be a second identity for the same thing and a join for nothing.
    - `Available` is get-only with no backing field, so EF ignores it by convention —
      but the DbContext still calls `Ignore(...)` explicitly (C2). Relying on a
      convention to keep a computed value out of a table is how a second source of
      truth appears quietly on an EF upgrade.
    - One Reservation row per (order, SKU), not per order: `ReleaseInventory` carries
      only the CorrelationId, so `OrderId` is the ONLY handle Inventory gets for undoing
      a hold. It is the column that gets the index.
    - `Released` is a tombstone, not a delete — a redelivered release must be able to
      see the hold is already gone and no-op instead of releasing stock twice.

[R] CRITICAL — Do NOT:
    1. Model Available as a stored column — it is derived. Storing it invites the two
       fields to disagree.
    IMPORTANT — Do NOT:
    2. Add validation attributes to Domain.
    3. Put transition logic (Held→Released, and the matching Reserved decrement) on the
       entity. It belongs in Business, in one transaction with the stock move. Marking a
       reservation Released without moving the stock level IS the silent-stock-loss bug.

[U] StockItem is the contended row. Reservation records what the saga is holding so
    a later ReleaseInventory can find and undo exactly the right hold. A hold that
    never got a Reservation row is stock nobody can ever release.

[B] N/A.
```

## Prompt C2 — Inventory reservation logic (the concurrency core) 🤖

```text
[S] Create the Data + Business layers for Inventory. The Business method that matters:
    ReserveAsync(Guid orderId, IReadOnlyList<(string Sku, int Qty)> lines, ct):
    for each line, atomically increment Reserved IF Available >= Qty, using optimistic
    concurrency (RowVersion). If any line cannot be satisfied, RELEASE any holds
    already taken for this order in this call and return a rejection. On full success,
    persist Reservation rows (Held) and return success.
    Also ReleaseAsync(Guid orderId, ct): flip this order's Held reservations to
    Released and decrement Reserved accordingly.

    Also create `Managers/DataContext/InventoryDbContext.cs` — the RowVersion config
    that [R]1 depends on lives in its OnModelCreating, so it cannot be a later prompt.

[C] - `stockItem.Property(x => x.RowVersion).IsRowVersion()` is THE load-bearing line
      of the whole service. It is what turns every stock write into
      `UPDATE StockItems SET Reserved=@new WHERE Sku=@sku AND RowVersion=@loaded`.
      Delete it and EF emits an unconditional UPDATE, both writers win, the SKU
      oversells, and nothing else in this prompt matters. Also: `Ignore(x => x.Available)`
      (C1), `HasKey(x => x.Sku)`, `HasIndex(OrderId, State)` on Reservation, and store
      ReservationState via `HasConversion<string>()` so the value is readable in the
      table during a demo and renumbering the enum cannot reinterpret old rows.
    - Use EF Core optimistic concurrency: on DbUpdateConcurrencyException, reload and
      RE-DECIDE — never reload and re-apply. The obvious implementation is wrong in a
      way that looks right: catch, reload, redo the same `Reserved += qty`, save. That
      reapplies a decision made against stock that has since moved — the oversell race
      with extra steps. Re-read, re-check `Available >= Qty`, and be fully prepared to
      come back with a rejection on the second pass. Losing the race is SUPPOSED to be
      able to change the answer.
    - The Reserved increment and the Reservation row go in the SAME SaveChangesAsync,
      so they share one transaction. Split them and a crash in between leaves stock held
      with no record of who holds it — unreleasable and invisible.
    - On DbUpdateConcurrencyException the Reservation you Added is still sitting in the
      change tracker as Added. DETACH it, or the NEXT line's SaveChanges quietly inserts
      it: a hold recorded against stock that was never reserved.
    - ReleaseAsync: group the order's Held reservations BY SKU and commit each group's
      stock decrement and tombstones together. A crash mid-release must never leave stock
      given back with the reservations still Held — the next redelivery would give the
      same stock back a second time.
    - Clamp Reserved at zero on release and log an ERROR if it would have gone negative
      ([R]3). A negative Reserved makes Available exceed OnHand and means a hold was
      released twice somewhere.
    - Return an OUTCOME enum from the Data layer (Held / InsufficientStock / UnknownSku /
      ConcurrencyExhausted), not a bool, so InventoryRejected.Reason can say which.
    - All-or-nothing per order: either every line is reserved or none remain held.
    - ReserveAsync begins by releasing any holds the order already carries — idempotency
      by reset. The consumer's (ConsumerName, MessageId) guard covers the normal
      redelivery, but the POC's idempotency store is in-memory, so a restart loses it;
      and no guard can help with a crash BETWEEN lines, which leaves real holds behind
      that no idempotency record ever knew about. Reserve from a known-empty state.
    - The intra-call unwind ([R]2) must reuse the SAME ReleaseAsync the saga's
      compensation calls. One release path, exercised on every rejected order rather
      than only on the rare compensation, so it cannot rot unnoticed.

[R] CRITICAL — Do NOT:
    1. Use a `SELECT` then unconditional `UPDATE` without the RowVersion check — that
       is the exact race that oversells the last unit. The update MUST be conditional
       on the version.
    2. Leave partial holds if a later line in the same order fails. Release what you
       took before returning a rejection.
    3. Let two concurrent reservations of the last unit both succeed. Exactly one wins;
       the loser gets a clean rejection, never a negative Available.
    IMPORTANT — Do NOT:
    4. Retry infinitely on concurrency conflict — bound it, then reject the line.
       Accept the consequence: under heavy contention a line with real stock can be
       rejected as ConcurrencyExhausted. That is the honest trade, and the reason string
       must distinguish it from genuine exhaustion.
    5. Throw for "insufficient stock" — that is a normal business outcome (rejection),
       not an exception. DO throw if the RELEASE itself fails: a tidy rejection returned
       while stock is still held would settle the message and make the leak permanent.

[U] Called by the Inventory consumer when it receives ReserveInventory. This is the
    "Concurrent purchase of the last unit" row of the Failure matrix — the thing you
    load-test on camera.

[B] N/A.
```

## Prompt C3 — Inventory consumers + reply events + wiring 🤖

```text
[S] Create:
    - Consumer for ReserveInventory: idempotency guard → Business.ReserveAsync →
      publish InventoryReserved OR InventoryRejected (with reason).
    - Consumer for ReleaseInventory: idempotency guard → Business.ReleaseAsync
      (no reply event needed; it is a compensation).
    - Facade, Controller (read-only ops endpoints: GET /api/Inventory for per-SKU
      availability), and Program.cs mirroring the Order wiring but referencing SQL +
      Service Bus (no Cosmos/Redis).
    - `InventoryDbInitializer` — EnsureCreated + seed, called BEFORE app.Run().

[C] - Both consumers derive from `ServiceBusConsumer<T>` (A5) and leave SubscriptionName
      null: reserve-inventory and release-inventory are QUEUES (commands, one handler),
      not topics.
    - **ReserveInventoryConsumer overrides `MaxConcurrentCalls` to 8.** Left at the
      default of 1, this service handles reservations strictly one at a time, two orders
      for the last unit NEVER overlap, and the row-version guard that C2 is entirely
      built around does not fire once. The concurrency demo passes for the wrong reason
      and proves nothing. Contention is the behaviour under test, so the consumer has to
      be able to contend with itself. Safe because each message gets its own DI scope
      and therefore its own DbContext. This is the ONLY consumer in the system that is
      deliberately not serialized.
    - The reply events carry deterministic MessageIds:
      `MessagingConventions.DeterministicMessageId(CorrelationId, nameof(InventoryReserved))`.
      Random ids and a retry's re-emitted reply looks brand new to the saga's guard,
      which then advances the order twice.
    - The consumer publishes the reply directly (Business returns a ReservationResult
      and does not touch the bus). This DIVERGES from Orders, where Business owns
      IMessageBus — a deliberate call, because here the consumer is the port that
      translates a decision onto the wire. Note it, or move it, but do not let the two
      services differ by accident.
    - Program.cs must create the schema and seed stock BEFORE `app.Run()` — hosted
      services start inside Run(), so this is what stops the reserve consumer taking its
      first message against a database with no tables. Seed only when empty (the AppHost
      gives SQL a persistent volume precisely so levels survive a restart). Include one
      SKU with OnHand = 1: without a last unit there is no last-unit demo. EnsureCreated,
      not migrations — right for a disposable POC container, wrong the moment the schema
      must change without losing data. Leave the TODO.

[R] CRITICAL — Do NOT:
    1. Orchestrate. Inventory replies with an event; it never calls Payment or the
       saga directly.
    2. Complete the ReserveInventory message before the reply event is published — if
       publish fails, let the message retry so the saga is never left waiting silently.
       The asymmetry is the point: publish-then-fail-to-settle is SAFE (the command
       redelivers, Inventory re-reserves from a clean state, and the deterministic
       MessageId makes the saga drop the duplicate reply). Settle-then-fail-to-publish
       is unrecoverable — stock held, saga waiting forever on an answer nobody will send
       again. Publishing inside HandleAsync gets this for free from the A5 base.
    IMPORTANT — Do NOT:
    3. Make ReleaseInventory fail loudly on an already-released order — releasing an
       order with no active holds is a valid no-op (idempotent compensation). A
       compensation that throws when it has nothing to do dead-letters itself out of
       existence the second time it is asked, which is exactly how stock gets stranded.
    4. Expose any endpoint that reserves or releases stock over HTTP. The Facade is
       read-only. Stock moves in response to saga commands off the bus and nothing else;
       an HTTP write path is a second, unaudited way around the whole compensation and
       idempotency machinery.

[U] Inventory hears ReserveInventory / ReleaseInventory from the saga and answers with
    InventoryReserved / InventoryRejected. Release has no reply — it is fire-and-forget
    compensation.

[B] N/A.
```

---

# PART D — Payment service (idempotent, duplicate-callback safe)

> Deltas from the Order template. Payment's reason to exist: idempotency against
> duplicate callbacks, and clean decline handling.
>
> **Run Prompt B1 first**, substituting `OrderFlow.Payments.API` (PLURAL — see B1 [C])
> and Payment's package list. D1 assumes the project already exists.
>
> Payment and Inventory look alike and are not. **Inventory guards a contended UPDATE
> with a row version; Payment guards a contended INSERT with a unique index.** Payment
> has no RowVersion, and that is a decision, not an omission — nothing updates a shared
> payment row, so there is nothing to version. The race is two duplicates both trying to
> CREATE the row, and only the database can adjudicate it.

## Prompt D1 — Payment domain + models 💬

```text
[S] Create:
    - `Managers/Domain/Payment.cs` — enum PaymentStatus { Pending=0, Captured=1,
      Declined=2, Refunded=3 }; class Payment: Id, OrderId, Amount (decimal),
      Status, AuthorizationCode (string), DeclineReason (string), IdempotencyKey
      (string), timestamps.
    - ServiceModels exposing per-order payment attempt history for the ops view.

[C] - Namespace `OrderFlow.Payments.API.Managers.Domain` — PLURAL. Singular
      `OrderFlow.Payment.API` plus the `Payment` entity above is the same CS0118
      namespace/type collision described in B1 [C].
    - All string properties initialized to `string.Empty`
    - DeclineReason must be STORED, not recomputed. `PaymentDeclined.Reason` has to be
      re-publishable verbatim when a duplicate ChargePayment is redelivered, and
      re-running the decline rule to reproduce it means that if the configured threshold
      moved in between, the replay yields a DIFFERENT reason — or approves a charge that
      was previously declined.
    - No RowVersion. Nothing updates a contended payment row; the contention is on the
      INSERT and the unique index on IdempotencyKey settles it (D2).
    - ServiceModel: mask the auth code (`AUTH-****3C4D`) and do NOT map IdempotencyKey.
      [R]3 of D3 forbids LOGGING the code above Debug; putting it unmasked in a
      browser-facing JSON response is a strictly wider exposure than the log line that
      restriction exists to prevent. Publishing the idempotency key tells a caller exactly
      what to send to collide with an existing charge on purpose.

[R] CRITICAL — Do NOT:
    1. Accept or store any card data — no PAN, CVV, expiry, or name. This POC
       simulates authorization; there is nothing to store but amount + auth code.
       Not masked, not encrypted, not "just for the demo". A field that does not exist
       cannot leak, cannot be logged by accident, and cannot drag PCI scope into a
       reference architecture.
    IMPORTANT — Do NOT:
    2. Default Status to anything but Pending on creation.

[U] The Payment row is keyed for idempotency so a duplicate ChargePayment (or a
    duplicate provider callback) resolves to the SAME row and the SAME outcome.

    NOTE — B8 sets `IdempotencyKey = order.Id.ToString("N")`, so an order can only ever
    have ONE payment row and "attempt history" is that row's lifecycle
    (Pending → Captured → Refunded), not a list of tries. That is what makes the ops view
    diagnostic: TWO rows for one order means the guard failed and the customer was charged
    twice. If you ever want genuinely retryable attempts, the key must vary per attempt
    and B8 changes with it.

    Also note `RefundPayment` carries no amount — refunds are all-or-nothing against the
    capture. Partial refunds are not expressible in the contract.

[B] N/A.
```

## Prompt D2 — Payment processing (idempotent charge) 🤖

```text
[S] Create Data + Business for Payment, plus `Managers/DataContext/PaymentDbContext.cs`
    (the unique index that [R]1 depends on lives in its OnModelCreating, so it cannot be
    a later prompt). The method that matters:
    ChargeAsync(Guid orderId, decimal amount, string idempotencyKey, ct):
    1. If a Payment with this idempotencyKey already exists AND has resolved, return its
       existing outcome UNCHANGED (no second charge).
    2. Otherwise INSERT a Pending row and let the unique index adjudicate — see [C].
       Then simulate authorization (AUTH-XXXXXXXX, 8 hex) and — for the demo — decline
       when a configurable rule says so (amount over a threshold, or a force-decline
       flag) so the compensation path is demonstrable.
    3. Set Captured or Declined, persist, return the outcome.
    Also RefundAsync(Guid orderId, ct): flip a Captured payment to Refunded (idempotent).

[C] - **The guard is the unique index, not the if-statement.** [S] step 1 as written is a
      read-then-insert race: two duplicate callbacks arriving at the same instant both read
      "no row", both authorize, both insert. No amount of C# in front of the database fixes
      that. `HasIndex(x => x.IdempotencyKey).IsUnique()` in OnModelCreating is what makes it
      correct — both racers insert, SQL rejects one (error 2601/2627 inside a
      DbUpdateException), and the loser reads back the winner's row and returns the WINNER'S
      outcome. Keep the existence check as the fast path for the common (sequential
      redelivery) case, but understand it is an optimisation, not the guarantee. Drop the
      index and "duplicate payment callback" stops being a no-op and starts being a second
      charge.
    - **A Pending row is not an outcome.** It means someone — us, a racer, or an attempt
      that crashed between INSERT and the authorization — created the row and has not
      recorded an answer. Resolving it is safe ONLY because the simulated authorization is
      DETERMINISTIC in the idempotency key: every party resolving the same Pending row
      computes the same code and the same decision, so a double-write writes identical
      values. Derive the code as `AUTH-{SHA256(idempotencyKey) first 8 hex}`. A random
      code satisfies "8 hex" and quietly breaks this: the row you end up with would depend
      on how many times the message happened to be redelivered. A real processor gives you
      this property by honouring the idempotency key server-side; simulate that contract
      rather than pretending it does not exist.
    - On the losing insert, DETACH the entity or the next SaveChanges retries it and throws
      again.
    - The idempotency key is supplied by the saga on ChargePayment and is stable across
      every redelivery of it, so retries and duplicate callbacks collapse to one row.
    - Simulated auth only — generate the code in C#, no HTTP to a real processor. Put it
      behind an `IPaymentAuthorizer` so the "there is no processor here" boundary is
      explicit and swappable.
    - Decline rules live in a bound options class (`DeclineOverAmount`, default 1000;
      `DeclineAll` for a live demo) — the failure-injection levers, not magic numbers.
    - Amount is rounded ONCE, on entry to ChargeAsync, `MidpointRounding.AwayFromZero`.
      Configure `HasPrecision(18, 2)` — left to convention EF picks a default and silently
      truncates the scale it does not expect.

[R] CRITICAL — Do NOT:
    1. Charge twice for the same idempotency key. A duplicate callback MUST be a no-op
       returning the first outcome — this is the "Duplicate payment callback" row of
       the Failure matrix.
    2. Call a real payment processor. Simulated authorization only.
    IMPORTANT — Do NOT:
    3. Treat a decline as an exception — it is a normal outcome that the saga
       compensates. Publish PaymentDeclined, don't throw. DO throw on a MISSING
       idempotency key: that is a broken caller, not a broken card, and without a key
       there is nothing to collapse duplicates onto, so every retry becomes a fresh charge.
    4. Round the amount mid-calculation — round only at the boundary.

[U] Called by the Payment consumer on ChargePayment / RefundPayment. Idempotency here
    is what makes at-least-once delivery safe for money.

[B] When refunding, ONLY change Status and timestamps. Do NOT alter Amount, OrderId,
    or AuthorizationCode. The auth code is the only evidence tying the refund back to the
    original capture — the thing an auditor asks to see. RefundAsync stays idempotent by
    looking for a CAPTURED payment: no payment, a declined one, or an already-refunded one
    finds nothing and quietly does nothing. A compensation that throws when it has nothing
    to do dead-letters itself the second time the saga asks.
```

## Prompt D3 — Payment consumers + reply events + wiring 🤖

```text
[S] Create:
    - Consumer for ChargePayment: idempotency guard → Business.ChargeAsync → publish
      PaymentSucceeded OR PaymentDeclined (with reason).
    - Consumer for RefundPayment: idempotency guard → Business.RefundAsync (no reply
      event — it is compensation).
    - Facade, Controller (read-only: GET /api/Payments/order/{orderId:guid} attempt
      history), Program.cs referencing SQL + Service Bus.
    - `PaymentDbInitializer` — EnsureCreated, called BEFORE app.Run(). No seed: every row
      is created by a charge.

[C] - Both consumers derive from `ServiceBusConsumer<T>` (A5) with SubscriptionName null:
      charge-payment and refund-payment are QUEUES.
    - **ChargePaymentConsumer overrides `MaxConcurrentCalls` to 4.** Same reasoning as C3:
      "duplicate payment callback" is this service's failure-matrix row, and its hardest
      form is two duplicates arriving at the SAME MOMENT — which a consumer pinned at 1
      can never produce, so the unique-index race from D2 would never once be exercised.
    - **The two idempotency guards are NOT redundant.** The base guards on (ConsumerName,
      MessageId) — but that store is in-memory in the POC, so a restart forgets it. The
      guard that protects the customer's money is D2's, keyed on ChargePayment.
      IdempotencyKey and enforced by a unique index in SQL. The first is an optimisation;
      the second is the guarantee. If you delete one, delete the first. Say so in the code,
      because the layering invites a reviewer to notice "duplicate" is checked twice and
      remove the expensive-looking database round trip — the wrong one.
    - Reply events carry deterministic MessageIds:
      `MessagingConventions.DeterministicMessageId(CorrelationId, nameof(PaymentSucceeded))`.
    - Program.cs runs EnsureCreated BEFORE app.Run(). This matters more here than in
      Inventory: EnsureCreated is what CREATES THE UNIQUE INDEX. Skip it and the service
      still starts, still accepts charges, and looks entirely healthy — while having
      silently lost its only real protection against a double charge. A missing table fails
      loudly; a missing index fails only when it matters.

[R] CRITICAL — Do NOT:
    1. Orchestrate. Payment replies with an event; it never calls Fulfillment or the
       saga directly.
    2. Complete the ChargePayment message before the reply event publishes. Publishing
       inside HandleAsync gets this for free from the A5 base. Re-charging on the retry is
       safe (the idempotency key resolves to the same row and returns the same outcome) and
       the deterministic reply id makes the saga drop the duplicate — whereas settling
       first and then failing to publish leaves the customer CHARGED and the saga waiting
       forever for an answer nobody will send again.
    IMPORTANT — Do NOT:
    3. Log the auth code above Debug level. And do not return it unmasked from the
       controller either — a JSON response is a wider exposure than the log line this rule
       exists to prevent (browser cache, proxy logs, anyone's DevTools). Mask to the last 4.
    4. Expose any endpoint that charges or refunds. The Facade is read-only. Money moves in
       response to saga commands off the bus and nothing else; an HTTP write path is a
       second, unauthenticated route to a customer's money that bypasses the idempotency
       key, the unique index, and the compensation machinery entirely.

[U] Payment hears ChargePayment / RefundPayment and answers with PaymentSucceeded /
    PaymentDeclined. Refund has no reply — fire-and-forget compensation.

[B] N/A.
```

---

# PART E — Fulfillment service (retry/backoff, dead-letter)

> Deltas. Fulfillment's reason to exist: resilient outbound calls to an unreliable
> dependency, and clean dead-lettering on hard failure.
>
> **Run Prompt B1 first**, substituting `OrderFlow.Fulfillment.API` (singular — no
> colliding entity) and its package list: bus only, no EF/SQL, no Cosmos/Redis.
> E1 assumes the project already exists.

## Prompt E1 — Simulated carrier client with resilience 🤖

```text
[S] Create `Managers/Business/CarrierClient.cs` (ICarrierClient) simulating a
    warehouse/carrier dispatch call, wrapped in a Polly resilience pipeline:
    retry-with-backoff for transient faults and a circuit breaker. The simulator is
    configurable to fail transiently (recovers on retry) or permanently (exhausts
    retries) so both paths are demonstrable.
    Also FulfillmentBusiness.DispatchAsync(Guid orderId, ct) that calls the client and
    returns dispatched / hard-failed.

[C] - Use Microsoft.Extensions.Http.Resilience / Polly. Tune the policy for THIS
      dependency (a few retries, short backoff, a breaker) — not a global default.
    - "Transient" vs "permanent" is driven by config/flag so the demo can choose.

[R] CRITICAL — Do NOT:
    1. Retry forever. Bounded retries, then treat it as a hard failure → the message
       will dead-letter.
    2. Retry a NON-transient failure (e.g. a 4xx-equivalent). Only transient faults
       are retried; permanent ones fail fast.
    IMPORTANT — Do NOT:
    3. Swallow the final failure. A hard failure must surface so the consumer lets the
       message dead-letter and the saga hears FulfillmentFailed.

[U] Called by the Fulfillment consumer on DispatchFulfillment. This is the "Payment
    gateway down" / "Carrier calls fail permanently" rows of the Failure matrix.

[B] N/A.
```

## Prompt E2 — Fulfillment consumer + dead-letter behavior + wiring 🤖

```text
[S] Create the DispatchFulfillment consumer: idempotency guard → DispatchAsync →
    publish FulfillmentDispatched on success, or FulfillmentFailed on hard failure.
    On a poison/undeserializable message or repeated handler failure, let it
    dead-letter after the configured max delivery count. Add Facade, a read-only
    ops Controller listing dead-lettered/stuck dispatches with reasons, and Program.cs
    (Service Bus only).

[C] - Rely on Service Bus max-delivery-count + DLQ rather than a hand-rolled retry
      loop in the consumer; the Polly policy handles in-call transient retries, the
      broker handles message-level redelivery, and the DLQ is the terminal home.
    - The ops endpoint reads from the DLQ (or a projection of it) so "stuck orders are
      visible with a reason" is literally true in the UI.

[R] CRITICAL — Do NOT:
    1. Catch-and-complete a message that failed to process. Completing a failed message
       hides the failure and strands the saga. Let it retry/dead-letter.
    2. Publish FulfillmentDispatched on a hard failure. The saga must hear the truth so
       it compensates (refund + release).
    IMPORTANT — Do NOT:
    3. Silently drop poison messages. They go to the DLQ with enough context to
       diagnose and replay.

[U] Fulfillment hears DispatchFulfillment and answers with FulfillmentDispatched /
    FulfillmentFailed. The DLQ visibility is the "stuck order" experience an ops lead
    asks about in week one.

[B] N/A.
```

---

# PART F — Notification service (best-effort, non-transactional)

> The smallest service, and a deliberate one. Its job is to prove a boundary:
> notification NEVER blocks or rolls back the order (see Services & Responsibilities,
> and Open Question re: whether it stays a separate service).
>
> **Run Prompt B1 first**, substituting `OrderFlow.Notification.API` (singular) and its
> package list: bus only. F1 assumes the project already exists.
>
> Notification is the one service that consumes TOPICS, not queues — so its consumers
> derive from `ServiceBusConsumer<T>` (A5) with `SubscriptionName => "notification"`, the
> subscription the AppHost declares on payment-declined, order-confirmed and order-failed.

## Prompt F1 — Notification consumer (best-effort) 🤖

```text
[S] Create a Notification service that subscribes to the customer-facing events
    (OrderConfirmed, OrderFailed, and optionally PaymentDeclined) and "sends" a
    simulated notification (log + a stored record for the ops/demo view). Consumer
    with idempotency guard; a simulated provider that can be toggled to fail. Facade,
    optional read-only Controller, Program.cs (Service Bus only).

[C] - Notification failure is retried best-effort a bounded number of times, then
      DROPPED — it must never feed back into the saga or block an order.
    - Simulated send only (log/record); no real email/SMS.

[R] CRITICAL — Do NOT:
    1. Publish any event that the saga consumes. Notification is a terminal subscriber
       — it reacts, it never replies into the workflow.
    2. Let a notification failure roll back, retry the order, or transition order state.
       Best-effort means the order is already done; this is fire-and-forget.
    IMPORTANT — Do NOT:
    3. Block the subscription pipeline on a slow provider — time-box the send.

[U] Notification hears OrderConfirmed / OrderFailed and informs the customer. This is
    the "Notification provider down" row: order proceeds unaffected, notification
    retried then dropped.

[B] N/A.
```

---

# PART G — Angular 20 frontend (customer status + ops)

## Prompt G1 — Angular workspace scaffold 🤖

```text
[S] Create `src/OrderFlow.Web/` with: package.json (Angular 20, start script using
    PORT env var defaulting to 4200), angular.json (application builder, standalone),
    tsconfig(.app).json (strict), src/main.ts (bootstrapApplication), src/index.html,
    src/styles.css with the CSS variables in [C], and a Dockerfile
    (node:22-alpine → nginx:alpine).

[C] - Standalone components only. NO NgModules. bootstrapApplication(AppComponent,
      appConfig).
    - Pin `"typescript": "~5.8.0"` (Angular 20 peer range >=5.8.0 <5.9.0; older pins
      throw verifySupportedTypeScriptVersion at build).
    - CSS variables in the Architect4Hire palette: --bg-deep #1a1a1a,
      --bg-surface #242424, --accent #c2410c (ember), --text-primary #f4f4f5,
      --success #7fb069, --danger #c9504a, --warn #d9a441.
    - src/environments/environment.ts with orderApi / inventoryApi / paymentApi URLs
      (from the AppHost-provided env vars).

[R] CRITICAL — Do NOT:
    1. Generate NgModules — Angular 20 standalone only.
    2. Add Material/PrimeNG/Bootstrap or any UI library. Hand-rolled CSS — this is an
       architecture demo; the UI is deliberately plain.
    3. Pin TypeScript to ~5.6/~5.7. Angular 20 needs 5.8.x — AI often grabs a stale
       "safe" version. Explicitly pin 5.8.
    IMPORTANT — Do NOT:
    4. Use the Webpack builder. Use @angular-devkit/build-angular:application.

[U] Served by Aspire as an npm app on :4200. Two surfaces only: a customer status
    view and an ops view. Plain by design.

[B] N/A.
```

## Prompt G2 — Core models + services 🤖

```text
[S] Create:
    - `src/app/core/models/models.ts` — interfaces matching the ServiceModels:
      PlaceOrder, OrderLine, OrderStatus (with state string), SkuAvailability,
      PaymentAttempt, StuckOrder.
    - `src/app/core/services/order.service.ts` — place(order), getStatus(id),
      listActive().
    - `src/app/core/services/inventory.service.ts` — listSkus().
    - `src/app/core/services/payment.service.ts` — getByOrder(id).
    Each `@Injectable({providedIn:'root'})`, inject(HttpClient), environment URLs,
    returning Observable<T>.

[C] - Angular 20 signal API where component state is involved; services return
      Observables.

[R] CRITICAL — Do NOT:
    1. Use NgRx/NGXS/Akita. Signals only for any shared state.
    IMPORTANT — Do NOT:
    2. Poll inside a service. Polling cadence is a component concern (Prompt G4/G5).

[U] Thin HTTP clients over the read-only + place-order endpoints. No cross-service
    calls from the browser — each service is hit directly per the AppHost env URLs.

[B] N/A.
```

## Prompt G3 — App shell + routes 🤖

```text
[S] Create app.config.ts (provideRouter, provideHttpClient(withFetch()),
    provideZoneChangeDetection({eventCoalescing:true})), app.routes.ts
    ('' → 'order', /order → CustomerOrderComponent lazy, /ops → OpsComponent lazy),
    and app.component.ts: a sticky top bar with the brand ("OrderFlow") and two nav
    links (Place Order, Ops), active state on the current route.

[R] Do NOT:
    1. Use ngFor/ngIf — use @for / @if control-flow.
    2. Add a third surface — two views is the whole frontend (Front End section).

[U] The shell stays mounted across navigation. Minimal by intent.

[B] N/A — new files.
```

## Prompt G4 — Customer order view (place + live status) 🤖

```text
[S] Create `src/app/features/order/customer-order.component.ts`:
    - A small form to place an order: customer ref + a few line rows (SKU, qty,
      unit price). Submit → orderService.place() → capture the returned order id.
    - A live status panel that polls orderService.getStatus(id) every 2s and renders
      the saga states as a progression: Placed → Reserved → Paid → Dispatched →
      Confirmed, or a Failed state showing FailureReason.
    - Stop polling once the order reaches Confirmed or Failed.

[C] - Use signals for orderId / status / polling handle. Clear the interval in
      ngOnDestroy AND on reaching a terminal state.
    - Render the state progression as a simple stepper; the current state is
      highlighted in --accent, a Failed terminal in --danger.

[R] CRITICAL — Do NOT:
    1. Assume synchronous completion. Placing returns State=Placed; the rest arrives
       over subsequent polls as the saga advances. The UI must handle the async
       progression, not expect a finished order from the POST.
    2. Keep polling after a terminal state — it wastes calls and muddies the trace.
    IMPORTANT — Do NOT:
    3. Use SignalR/WebSockets. Polling is intentional for this POC (a TODO notes the
       future SignalR move).

[U] The customer surface. Its whole job is to make the saga's asynchronous progression
    visible in real time — the same story the distributed trace tells, in the UI.

[B] N/A.
```

## Prompt G5 — Ops view (inventory, payments, stuck orders) 🤖

```text
[S] Create `src/app/features/ops/ops.component.ts` with three panels:
    - Inventory: per-SKU OnHand / Reserved / Available (inventoryService.listSkus,
      poll 5s).
    - Active orders: the non-terminal orders (orderService.listActive, poll 5s) with
      their current state.
    - Stuck / dead-lettered: orders sitting in a failed-dispatch/DLQ state WITH the
      reason (from the fulfillment ops endpoint).

[C] - Signals + @for/@if. Clear intervals in ngOnDestroy.
    - Available <= 0 rendered in --danger; stuck rows in --warn with the reason shown.

[R] CRITICAL — Do NOT:
    1. Offer any write action here (no manual state edits, no forced releases). Ops is
       read-only observation for the demo.
    IMPORTANT — Do NOT:
    2. Hide the failure reason on stuck orders — the reason IS the value of this panel.

[U] The ops surface answers the first question a real operations lead asks: "show me
    what's stuck and why." Making that visible is worth more than any styling.

[B] N/A.
```

---

# PART H — Wire-up, run, and the failure-injection walkthrough

## Prompt H1 — Wire AppHost project references ✏️

```text
[S] - Edit `src/OrderFlow.AppHost/OrderFlow.AppHost.csproj` to add ProjectReference
      items for the five API csproj files now that they exist.
    - Edit `src/OrderFlow.AppHost/Program.cs` to UNCOMMENT the five AddProject blocks
      and the Angular `.WithEnvironment(...)`/`.WaitFor(...)` lines that depend on
      them. Prompt A3 could not emit those live: the `Projects.*` types are SOURCE-
      GENERATED from the ProjectReference items above, so they do not exist until this
      step. They were written out in full and commented, so this is a pure uncomment.

[C] Two-file edit. Keep existing PackageReference items unchanged.

[R] CRITICAL — Do NOT:
    1. Add a ProjectReference to ServiceDefaults or Contracts from the AppHost — the
       APIs reference those; the AppHost doesn't need them directly.
    2. Add a ProjectReference to OrderFlow.Web — it's an npm app, not a .NET project.

[B] B-DOMINANT (Edit Mode):
    - In the csproj: ONLY ADD the five ProjectReference lines.
    - In Program.cs: ONLY remove the leading `// ` from the already-written AddProject
      and Angular env-var lines. Do NOT re-author them — if a line does not compile
      once uncommented, say so rather than rewriting it.
    - Do NOT modify any other existing element in either file.
    - Do NOT reformat unrelated lines.
    - All other project state must be byte-for-byte identical.
```

## Prompt H2 — Happy-path sanity check ✏️

```text
[S] Run `dotnet build`, then `dotnet run --project src/OrderFlow.AppHost`. Verify in
    the Aspire dashboard that SQL, Cosmos emulator, Redis, Service Bus emulator, and
    all five APIs reach Healthy. Open the web app at :4200, place an order with in-
    stock SKUs and a normal amount, and confirm it advances Placed → Reserved → Paid
    → Dispatched → Confirmed. Then open the Aspire trace for that order and confirm it
    is ONE distributed trace spanning all services.

[R] If any step fails:
    CRITICAL — Do NOT:
    1. Say "should work" — paste the actual error from the dashboard/logs.
    IMPORTANT — Do NOT:
    2. Fix multiple issues at once. Use the iterative refinement loop (Identify →
       Diagnose → Refine → Re-execute → Verify, slide 26).
    3. Disable health checks or idempotency to mask a startup/timing issue.

[B] Reading/running only. Do NOT edit code unless a failure is reproduced.
```

## Prompt H3 — Failure-injection walkthrough (the demo) ✏️

```text
[S] Exercise each row of the Failure Handling matrix and confirm the expected
    behavior, capturing the trace each time:
    1. Duplicate payment callback → redeliver ChargePayment; confirm ONE charge,
       no duplicate transition.
    2. Concurrent last-unit purchase → fire two orders for the last unit
       simultaneously; confirm exactly one Reserved, one cleanly Failed, no oversell.
    3. Payment gateway transient down → confirm retry/backoff recovers.
    4. Carrier hard failure → confirm dead-letter + a stuck order visible with reason
       in the ops view, AND the saga compensates (refund + release).
    5. Payment declined after reservation → confirm reservation released, order Failed.
    6. Notification provider down → confirm the order still Confirms unaffected.
    7. Poison message → confirm dead-letter after max delivery, replayable.

[C] Drive failures via the configurable simulators (decline rule, transient/permanent
    carrier flag) and by redelivering/poisoning messages — NOT by editing service code
    mid-demo.

[R] CRITICAL — Do NOT:
    1. Declare a scenario "passing" from logs alone — show the trace and the ops view
       state that proves it.
    2. Leave any order with inventory still reserved after a compensation. If you find
       one, that is a real bug in the saga (Prompt B8), not a demo glitch — fix it there.
    IMPORTANT — Do NOT:
    3. Fix more than one failing scenario per iteration.

[B] These are demonstrations, not edits. Code changes only if a scenario reproduces a
    genuine defect.
```

---

# PART I — Diagnostic prompts (use when output is wrong)

> The five failure modes from slide 25, plus the ones an EVENT-DRIVEN build adds:
> lost messages, non-idempotent consumers, orchestration leaking out of the saga, and
> compensation that doesn't fully compensate. Drop these into chat when something goes
> sideways.

### I1 — "AI implemented business rules I didn't ask for"

```text
[S] Re-review `<path>`. Identify any logic not explicitly in the original [S] block —
    especially pricing/tax/discount/eligibility rules, or extra order states.

[R] CRITICAL — Do NOT:
    1. Add pricing, tax, discount, loyalty, or fraud logic anywhere yet. POC scope is
       fixed to the fulfillment saga.
    Replace any such code with `// TODO: not in POC scope`.

[B] Do NOT change method signatures or interfaces. Only remove or stub surprise logic.
```

### I2 — "Output doesn't match our architecture"

```text
[S] Compare `<path>` against `src/OrderFlow.Orders.API/` for layering. Order is the
    architectural reference.

[C] Strict onion: Controller/Consumer → Facade → Business → Data → DbContext/Store.
    Mapping in Extensions. Dependencies point inward only. Services share ONLY
    Contracts.

[R] CRITICAL — Do NOT:
    1. Allow a Controller/Consumer to inject IBusinessManager, IDataManager, a
       DbContext, or the bus directly (Consumers may inject the idempotency store +
       the saga/business entry point only).
    2. Allow a Facade to inject a Data manager, DbContext, or SDK client.
    3. Allow a Business class to import EF Core / Cosmos / Redis / Service Bus SDK
       namespaces.
    4. Allow a ProjectReference between two services.

[B] Refactor in place — file paths and public class names stay identical.
```

### I3 — "Plausible but incorrect logic" (Plausibility Trap)

```text
[S] Walk me through `<method>` step-by-step with a worked example. For reservation:
    OnHand=1, two orders each want Qty=1, arriving concurrently. State each read,
    each RowVersion check, and each write outcome. Show why exactly one succeeds.

[R] Do NOT change code yet. Diagnostic only.

[B] N/A — analytical step.
```

### I4 — "Consumer isn't idempotent / message got processed twice"

```text
[S] Trace `<consumer>`'s handling of a REDELIVERED message. Confirm: (a) the
    idempotency key is (ConsumerName, MessageId); (b) the processed-marker is written
    in the same logical unit as the side effect, or the side effect is itself
    idempotent; (c) a redelivery after a successful first handling is a no-op.

[R] CRITICAL — Do NOT:
    1. "Fix" idempotency by completing the message earlier — that trades double-
       processing for message loss. The guard is the fix, not earlier acking.

[B] If a change is needed, change ONLY the guard/marker logic. Do NOT alter the
    business method's behavior on first delivery.
```

### I5 — "Compensation didn't fully compensate"

```text
[S] For the failure path `<PaymentDeclined | FulfillmentFailed>`, enumerate every
    side effect that occurred BEFORE the failure (reservation held? payment captured?)
    and confirm the saga issues a compensating action for EACH one, in an order that
    leaves no resource held. Then confirm each compensation is idempotent under
    redelivery.

[R] CRITICAL — Do NOT:
    1. Consider the path correct if inventory can remain reserved or a capture can
       remain un-refunded on ANY interleaving. That is the core defect this system
       exists to prevent.

[B] Fixes go in the saga (Prompt B8) only. Do NOT push compensation logic into
    consumers or reacting services.
```

### I6 — "Orchestration leaked out of the saga"

```text
[S] Review `<reacting service>` (Inventory/Payment/Fulfillment/Notification). Confirm
    it ONLY replies with events and NEVER sends the next command or calls another
    service. The saga is the sole orchestrator.

[R] CRITICAL — Do NOT:
    1. Allow a reacting service to send a command that advances the workflow, or to
       call another service's API. It replies with a past-tense event; the saga decides
       what happens next.

[B] Refactor to reply-only. Do NOT change the event payloads the saga depends on.
```

### I7 — "Agent mode created files I didn't expect"

```text
[S] List every file you created in the last action. For each, state whether it was in
    my original [S] block.

[R] CRITICAL — Do NOT:
    1. Delete anything yet. Wait for my confirmation.

AGENT MODE ADDITION (next attempt):
    - Restrict file creation to these directories: <list>
    - Do NOT create files outside these directories.
    - Do NOT add a message type to Contracts without it being named in [S].
    - Verify the build passes before declaring complete.
```

---

# PART J — Authoring the ADRs

> The design doc lists five ADRs by title only. These prompts turn each into the
> short record the doc promises ("the full ADRs live alongside the code"). Run them
> after the build so the rationale reflects what was actually built.

## Prompt J1 — Generate the five ADR stubs 💬

```text
[S] Create `docs/adr/ADR-001..005.md`, one file each, in the standard ADR shape:
    Title, Status (Accepted), Context, Decision, Consequences, Alternatives Considered.
    Titles/decisions from the design doc:
    - ADR-001 Saga with compensation (rejected: 2PC / distributed transaction)
    - ADR-002 Cosmos DB for the append-only event log (rejected: single relational store)
    - ADR-003 Light CQRS read model for status (rejected: full event sourcing)
    - ADR-004 No separate warehouse-management service (rejected: decomposing fulfillment)
    - ADR-005 Aspire-local emulation with real Azure SDKs (rejected: live Azure for demo)

[C] Keep each ADR under one page. Consequences must include the DOWNSIDE we accepted,
    not just the upside — an ADR with no cost listed isn't a real decision record.

[R] CRITICAL — Do NOT:
    1. Write these as marketing. State the rejected option fairly and the cost of the
       chosen one honestly — that honesty is the "right-sizing" thesis in action.
    IMPORTANT — Do NOT:
    2. Invent decisions not in the design doc. Five ADRs, these five.

[U] These are read by a technical evaluator deciding whether the author reasons about
    tradeoffs or just assembles patterns. They ARE the differentiator.

[B] N/A — new files.
```

## Prompt J2 — Cross-check ADRs against the code ✏️

```text
[S] For each ADR, verify the code actually reflects it and add a "Verification" line
    citing the file(s) that implement the decision (e.g. ADR-001 → Managers/Saga/
    OrderSaga.cs compensation methods; ADR-002 → OrderEventStore partitioned by
    OrderId).

[R] Do NOT alter the code to match an ADR here. If they disagree, note the mismatch as
    an open item — reconciling it is a separate decision.

[B] Edit the ADR files only. One "Verification:" line per ADR.
```

---

# How this maps back to the design doc & the SCRUB deck

| Design-doc section             | Where it lives in this library                                                 |
| ------------------------------ | ------------------------------------------------------------------------------ |
| Purpose / Design Principles    | Phase 0 pre-load (U + the CRITICAL R block encode the principles)              |
| Scope (In / Out)               | PART A–G build only what's In Scope; Out-of-Scope items are `// TODO`s         |
| System Overview / Logical Flow | PART B (saga) + the per-service reply events in C/D/E/F                        |
| Services & Responsibilities    | One PART per service (B Order, C Inventory, D Payment, E Fulfillment, F Notif) |
| Saga & Compensation            | Prompt B8 (the centerpiece) + I5 diagnostic                                    |
| Failure Handling & Resilience  | Prompts C2, D2, E1, E2, F1 + the PART H3 walkthrough + I4/I5 diagnostics       |
| Data & Persistence             | Prompts B5 (Cosmos), B6 (Redis), C1 (SQL concurrency)                          |
| Messaging & Contracts          | Prompts A2 (Contracts) + A5 (bus/idempotency/correlation)                      |
| Front End                      | PART G (G4 customer status, G5 ops)                                            |
| Observability                  | Prompt A4 (messaging trace wiring) + H2/H3 (verify one trace per order)        |
| Architecture Decision Records  | PART J                                                                         |
| Build Phases                   | The A→B→C→D→E→F→G→H ordering IS the "ship in slices" sequence                  |

| Deck slide                                 | Pattern used here                                                              |
| ------------------------------------------ | ------------------------------------------------------------------------------ |
| Slide 7 — SCRUB Five Elements              | Every prompt is `[S][C][R][U][B]`                                              |
| Slide 8 — Tight vs Vague Scope             | Each `[S]` names exact methods/events/routes/files                             |
| Slide 9 — Constraints match architecture   | Versions + messaging discipline pinned via the pre-loaded CLAUDE.md            |
| Slide 10/11 — Restrictions, HRIS-style     | Tiered CRITICAL/IMPORTANT/PREFERRED throughout                                 |
| Slide 13 — Behavior dominant in Edit Mode  | Prompts H1, J2, and the I-series enforce "only change X"                       |
| Slide 17 — Agent Mode Addition             | 🤖 prompts include file-placement + Contracts-drift constraints                |
| Slide 21 — Prompt Chaining                 | A→B→…→J is Context → Contracts → Saga → Reactors → UI → Verify → Document      |
| Slide 22 — Tiered Negative Specification   | Every [R] block uses the three tiers                                           |
| Slide 23 — Layered SCRUB                   | Per service: Domain → Mapping → Data → Business → Facade → Consumer/Controller |
| Slide 25 — Five Failure Modes              | The I-series matches each, PLUS event-driven-specific I4/I5/I6                 |
| Slide 26 — Iterative Refinement Loop       | Prompts H2/H3 explicitly invoke the loop on failure                            |
| Slide 29/30 — Custom Instructions pre-load | Phase 0 `CLAUDE.md` does exactly this                                          |

> **The key insight from slide 33:** *"They know what to exclude."*
> In an event-driven build the exclusions are load-bearing: no 2PC, no orchestration
> outside the saga, no consumer without an idempotency guard, no compensation that
> leaves a resource held. Notice how much of each `[R]` is about what NOT to let the
> distributed system do. That discipline is the whole demo.
