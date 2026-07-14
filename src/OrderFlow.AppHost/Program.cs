var builder = DistributedApplication.CreateBuilder(args);

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

// ─────────────────────────────────────────────────────────────────────────────
// Redis — the order status read model (ADR-003). A projection, not the system of
// record: it can be rebuilt from the Cosmos event stream.
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
serviceBus.AddServiceBusQueue("reserve-inventory");
serviceBus.AddServiceBusQueue("release-inventory");     // compensation
serviceBus.AddServiceBusQueue("charge-payment");
serviceBus.AddServiceBusQueue("refund-payment");        // compensation
serviceBus.AddServiceBusQueue("dispatch-fulfillment");

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
//
// TODO (Prompt H1): uncomment once the five API projects exist and the csproj has
// their ProjectReference items. The Projects.* types below are SOURCE-GENERATED
// from those ProjectReferences, so this block cannot compile until then.
//
// var orderApi = builder.AddProject<Projects.OrderFlow_Orders_API>("order-api")
//     .WithReference(orderEvents).WaitFor(orderEvents)   // the container, not the database:
//                                                        // AddAzureCosmosContainer resolves a
//                                                        // Container straight from this name.
//     .WithReference(redis).WaitFor(redis)
//     .WithReference(serviceBus).WaitFor(serviceBus)
//     .WithExternalHttpEndpoints();
//
// var inventoryApi = builder.AddProject<Projects.OrderFlow_Inventory_API>("inventory-api")
//     .WithReference(inventoryDb).WaitFor(inventoryDb)
//     .WithReference(serviceBus).WaitFor(serviceBus)
//     .WithExternalHttpEndpoints();
//
// var paymentApi = builder.AddProject<Projects.OrderFlow_Payments_API>("payment-api")
//     .WithReference(paymentDb).WaitFor(paymentDb)
//     .WithReference(serviceBus).WaitFor(serviceBus)
//     .WithExternalHttpEndpoints();
//
// builder.AddProject<Projects.OrderFlow_Fulfillment_API>("fulfillment-api")
//     .WithReference(serviceBus).WaitFor(serviceBus)
//     .WithExternalHttpEndpoints();
//
// builder.AddProject<Projects.OrderFlow_Notification_API>("notification-api")
//     .WithReference(serviceBus).WaitFor(serviceBus)
//     .WithExternalHttpEndpoints();

// ─────────────────────────────────────────────────────────────────────────────
// Angular — the customer status view and the ops view. API URLs arrive as env
// vars; nothing is baked into the bundle.
// ─────────────────────────────────────────────────────────────────────────────
builder.AddJavaScriptApp("web", "../OrderFlow.Web", "start")
    .WithHttpEndpoint(port: 4200, env: "PORT")
    // TODO (Prompt H1): wire these once the API resources above are uncommented.
    // .WithEnvironment("ORDER_API_URL", orderApi.GetEndpoint("http"))
    // .WithEnvironment("INVENTORY_API_URL", inventoryApi.GetEndpoint("http"))
    // .WithEnvironment("PAYMENT_API_URL", paymentApi.GetEndpoint("http"))
    // .WaitFor(orderApi)
    .PublishAsDockerFile();

builder.Build().Run();
