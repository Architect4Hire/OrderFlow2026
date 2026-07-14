var builder = DistributedApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// Failure-injection levers. Every "what happens when X breaks" scenario in the
// demo is driven from here, so nobody has to edit code or appsettings mid-talk.
// Defaults live in appsettings.Development.json; override with:
//   dotnet run -- --Parameters:carrier-failure-mode=Permanent
// or `dotnet user-secrets set Parameters:payment-decline-all true`.
// ─────────────────────────────────────────────────────────────────────────────
var carrierFailureMode = builder.AddParameter("carrier-failure-mode");
var paymentDeclineAll = builder.AddParameter("payment-decline-all");
var paymentDeclineOverAmount = builder.AddParameter("payment-decline-over-amount");
var notificationProviderDown = builder.AddParameter("notification-provider-down");
var notificationProviderHangs = builder.AddParameter("notification-provider-hangs");

// ─────────────────────────────────────────────────────────────────────────────
// SQL Server — relational stores for Inventory (stock/reservations) and Payment.
// Persistent volume + persistent container so stock levels survive a restart and
// the concurrency demo has real data to contend over.
//
// NO PASSWORD PARAMETER, deliberately. Passing null lets Aspire generate a random
// administrator password and persist it to USER SECRETS (the AppHost declares a
// UserSecretsId), so it is stable across runs, unique per machine, and never enters the
// repository. A fresh clone still comes up on `dotnet run` with no setup step.
//
// This replaces an explicit `AddParameter("sql-password", secret: true)` whose value was
// supplied by a plaintext default in the committed appsettings.Development.json. That
// honoured A3 [R]'s letter — the parameter WAS marked secret — while defeating its
// purpose, because the secret sat in git. `secret: true` protects a value from being
// logged; it does nothing about where you chose to write it down.
// ─────────────────────────────────────────────────────────────────────────────
var sql = builder.AddSqlServer("sql")
    .WithDataVolume("orderflow-sql-data")
    .WithLifetime(ContainerLifetime.Persistent);

var inventoryDb = sql.AddDatabase("InventoryDb");
var paymentDb = sql.AddDatabase("PaymentDb");

// ─────────────────────────────────────────────────────────────────────────────
// Cosmos DB — the append-only order event log, partitioned by OrderId (ADR-002).
// TODO: swap RunAsEmulator for live account via config
// ─────────────────────────────────────────────────────────────────────────────
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator(emulator => emulator.WithDataVolume("orderflow-cosmos-data"));

var orderEventsDb = cosmos.AddCosmosDatabase("OrderEventsDb");

// The event stream itself. Partition key is "/orderId", so one order's whole history lives in
// one partition and ReadStream never fans out across partitions (ADR-002). The camelCase path
// is load-bearing: the Order API configures its Cosmos client with the System.Text.Json
// serializer so OrderId serializes to "orderId" and matches this exactly.
var orderEvents = orderEventsDb.AddContainer("order-events", "/orderId");

// The saga's durable idempotency store. Partitioned by consumer so the (ConsumerName, MessageId)
// key is a partition-key + id lookup: one point read, and an insert collision IS the duplicate
// check. In-memory would forget every processed message on restart.
var processedMessages = orderEventsDb.AddContainer("processed-messages", "/consumerName");

// ─────────────────────────────────────────────────────────────────────────────
// Redis — the order status read model (ADR-003). A projection, not the system of
// record: it can be rebuilt from the Cosmos event stream (and now actually is —
// POST /api/Orders/rebuild-projection).
// ─────────────────────────────────────────────────────────────────────────────
var redis = builder.AddRedis("redis");

// ─────────────────────────────────────────────────────────────────────────────
// Service Bus — the spine of the saga.
// Commands go to QUEUES (exactly one handler). Events go to TOPICS with one
// SUBSCRIPTION per interested service (many subscribers).
// TODO: swap RunAsEmulator for live account via config
// ─────────────────────────────────────────────────────────────────────────────
var serviceBus = builder.AddAzureServiceBus("servicebus")
    .RunAsEmulator();

// Commands — saga → reacting service. One handler each.
//
// MaxDeliveryCount is tuned DOWN from the default of 10. At the default, a message that keeps
// failing takes ten rounds of lock-expiry and backoff to reach the dead-letter queue — minutes of
// dead air in a demo whose entire point is showing you the dead-letter queue. Four attempts is
// still enough to ride out a transient blip.
serviceBus.AddServiceBusQueue("reserve-inventory").WithProperties(queue => queue.MaxDeliveryCount = 4);
serviceBus.AddServiceBusQueue("release-inventory").WithProperties(queue => queue.MaxDeliveryCount = 4);
serviceBus.AddServiceBusQueue("commit-inventory").WithProperties(queue => queue.MaxDeliveryCount = 4);
serviceBus.AddServiceBusQueue("charge-payment").WithProperties(queue => queue.MaxDeliveryCount = 4);
serviceBus.AddServiceBusQueue("refund-payment").WithProperties(queue => queue.MaxDeliveryCount = 4);
serviceBus.AddServiceBusQueue("dispatch-fulfillment").WithProperties(queue => queue.MaxDeliveryCount = 4);

// Events — reacting service → saga. The saga is the only subscriber.
//
// TWO names, and they are not the same thing. AddServiceBusSubscription(name, subscriptionName) takes an
// ASPIRE RESOURCE name first — which is global across the entire app model, not scoped to its topic — and
// the BROKER subscription name second. Passing one argument makes them identical, so five topics each
// tried to register a resource literally called "order-saga" and the host threw on the second one before
// a single container started. The consumers bind to the broker name, which must stay "order-saga" and
// "notification"; only the resource name has to be unique, hence the "{topic}-{subscriber}" prefix.
//
// This never failed a build. It failed at startup, which is the only place it could have been caught.
const string saga = "order-saga";
const string notify = "notification";

serviceBus.AddServiceBusTopic("inventory-reserved").AddServiceBusSubscription("inventory-reserved-saga", saga);
serviceBus.AddServiceBusTopic("inventory-rejected").AddServiceBusSubscription("inventory-rejected-saga", saga);
serviceBus.AddServiceBusTopic("payment-succeeded").AddServiceBusSubscription("payment-succeeded-saga", saga);
serviceBus.AddServiceBusTopic("fulfillment-dispatched").AddServiceBusSubscription("fulfillment-dispatched-saga", saga);
serviceBus.AddServiceBusTopic("fulfillment-failed").AddServiceBusSubscription("fulfillment-failed-saga", saga);

// PaymentDeclined has two subscribers: the saga compensates, Notification informs. This is the one topic
// that proves the topic/subscription choice was worth making — a queue could not fan out to both.
var paymentDeclined = serviceBus.AddServiceBusTopic("payment-declined");
paymentDeclined.AddServiceBusSubscription("payment-declined-saga", saga);
paymentDeclined.AddServiceBusSubscription("payment-declined-notification", notify);

// Terminal events — Notification is a terminal subscriber. Nothing replies to these.
serviceBus.AddServiceBusTopic("order-confirmed").AddServiceBusSubscription("order-confirmed-notification", notify);
serviceBus.AddServiceBusTopic("order-failed").AddServiceBusSubscription("order-failed-notification", notify);

// There is deliberately NO "order-placed" topic.
//
// One used to be declared here, with a comment saying OrderPlaced was "published for the audit trail".
// It was not published at all: OrderBusinessManager.PlaceAsync APPENDS OrderPlaced to the Cosmos event
// log and then sends ReserveInventory. Nothing ever wrote to the topic and nothing ever subscribed to
// it. The audit trail is the event log (ADR-002) — that was always the design; the topic was a
// misremembering of it.
//
// Dead infrastructure is not free. **The Service Bus emulator refuses to start a topic that has zero
// subscriptions** ("At least one subscription required per topic"), so this unused, unpublished,
// unsubscribed topic segfaulted the broker on boot — and with the broker down, nothing in the system
// works. It cost nothing to declare and took out the entire message bus.

// ─────────────────────────────────────────────────────────────────────────────
// APIs — least privilege: each service gets ONLY the resources it uses.
// ─────────────────────────────────────────────────────────────────────────────
var orderApi = builder.AddProject<Projects.OrderFlow_Orders_API>("order-api")
    .WithReference(orderEvents).WaitFor(orderEvents)          // the CONTAINER, not the database:
                                                              // AddAzureCosmosContainer resolves a
                                                              // Container straight from this name.
    .WithReference(processedMessages).WaitFor(processedMessages)
    .WithReference(redis).WaitFor(redis)
    .WithReference(serviceBus).WaitFor(serviceBus)
    .WithExternalHttpEndpoints();

var inventoryApi = builder.AddProject<Projects.OrderFlow_Inventory_API>("inventory-api")
    .WithReference(inventoryDb).WaitFor(inventoryDb)
    .WithReference(serviceBus).WaitFor(serviceBus)
    .WithExternalHttpEndpoints();

var paymentApi = builder.AddProject<Projects.OrderFlow_Payments_API>("payment-api")
    .WithReference(paymentDb).WaitFor(paymentDb)
    .WithReference(serviceBus).WaitFor(serviceBus)
    .WithEnvironment("Payment__DeclineAll", paymentDeclineAll)
    .WithEnvironment("Payment__DeclineOverAmount", paymentDeclineOverAmount)
    .WithExternalHttpEndpoints();

var fulfillmentApi = builder.AddProject<Projects.OrderFlow_Fulfillment_API>("fulfillment-api")
    .WithReference(serviceBus).WaitFor(serviceBus)
    .WithEnvironment("Carrier__FailureMode", carrierFailureMode)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.OrderFlow_Notification_API>("notification-api")
    .WithReference(serviceBus).WaitFor(serviceBus)
    .WithEnvironment("Notification__ProviderDown", notificationProviderDown)
    .WithEnvironment("Notification__ProviderHangs", notificationProviderHangs)
    .WithExternalHttpEndpoints();

// ─────────────────────────────────────────────────────────────────────────────
// Angular — the customer status view and the ops view (Part G).
//
// AddJavaScriptApp resolves its directory EAGERLY. While src/OrderFlow.Web did not exist, this line
// made the AppHost throw on startup — so from Part B until Part G the solution built green every
// time and the host could never once have started. A green build is not a running system.
//
// The port is pinned to 4200 rather than left to Aspire because every API's CORS policy whitelists
// exactly one origin, http://localhost:4200 (B11 [R]1 — never a wildcard). A dynamically-assigned
// port would serve the app from an origin the APIs refuse, and every call the browser made would be
// blocked — a dead UI in front of five perfectly healthy services.
// ─────────────────────────────────────────────────────────────────────────────
builder.AddJavaScriptApp("web", "../OrderFlow.Web", "start")
    .WithNpm()                                    // runs `npm install` before start, so a fresh clone
                                                  // comes up with `dotnet run` and nothing else.
    .WithHttpEndpoint(port: 4200, env: "PORT")
    // The browser cannot read an environment variable. These reach it as a generated config.js —
    // see OrderFlow.Web/scripts/write-config.mjs.
    .WithEnvironment("ORDER_API_URL", orderApi.GetEndpoint("http"))
    .WithEnvironment("INVENTORY_API_URL", inventoryApi.GetEndpoint("http"))
    .WithEnvironment("PAYMENT_API_URL", paymentApi.GetEndpoint("http"))
    .WithEnvironment("FULFILLMENT_API_URL", fulfillmentApi.GetEndpoint("http"))
    .WaitFor(orderApi)
    .PublishAsDockerFile();

builder.Build().Run();
