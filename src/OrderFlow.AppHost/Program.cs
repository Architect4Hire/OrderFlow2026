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
// ─────────────────────────────────────────────────────────────────────────────
var sqlPassword = builder.AddParameter("sql-password", secret: true);

var sql = builder.AddSqlServer("sql", sqlPassword)
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
serviceBus.AddServiceBusTopic("inventory-reserved").AddServiceBusSubscription("order-saga");
serviceBus.AddServiceBusTopic("inventory-rejected").AddServiceBusSubscription("order-saga");
serviceBus.AddServiceBusTopic("payment-succeeded").AddServiceBusSubscription("order-saga");
serviceBus.AddServiceBusTopic("fulfillment-dispatched").AddServiceBusSubscription("order-saga");
serviceBus.AddServiceBusTopic("fulfillment-failed").AddServiceBusSubscription("order-saga");

// PaymentDeclined has two subscribers: the saga compensates, Notification informs.
var paymentDeclined = serviceBus.AddServiceBusTopic("payment-declined");
paymentDeclined.AddServiceBusSubscription("order-saga");
paymentDeclined.AddServiceBusSubscription("notification");

// Terminal events — Notification is a terminal subscriber. Nothing replies to these.
serviceBus.AddServiceBusTopic("order-confirmed").AddServiceBusSubscription("notification");
serviceBus.AddServiceBusTopic("order-failed").AddServiceBusSubscription("notification");

// OrderPlaced is published for the audit trail. No service reacts to it today —
// the saga starts by SENDING ReserveInventory, not by consuming this event.
serviceBus.AddServiceBusTopic("order-placed");

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
// Angular — the customer status view and the ops view.
//
// TODO (Prompt G1): uncomment once src/OrderFlow.Web exists. AddJavaScriptApp resolves the
// directory EAGERLY, so leaving this active before the workspace is scaffolded makes the AppHost
// fail on startup — which is precisely what it did, silently, for the whole of Parts B–F: the
// solution built green the entire time and the host could never once have started.
// ─────────────────────────────────────────────────────────────────────────────
// builder.AddJavaScriptApp("web", "../OrderFlow.Web", "start")
//     .WithHttpEndpoint(port: 4200, env: "PORT")
//     .WithEnvironment("ORDER_API_URL", orderApi.GetEndpoint("http"))
//     .WithEnvironment("INVENTORY_API_URL", inventoryApi.GetEndpoint("http"))
//     .WithEnvironment("PAYMENT_API_URL", paymentApi.GetEndpoint("http"))
//     .WithEnvironment("FULFILLMENT_API_URL", fulfillmentApi.GetEndpoint("http"))
//     .WaitFor(orderApi)
//     .PublishAsDockerFile();

builder.Build().Run();
