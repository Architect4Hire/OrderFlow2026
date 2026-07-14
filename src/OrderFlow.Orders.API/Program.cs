using OrderFlow.Orders.API.Managers.Business;
using OrderFlow.Orders.API.Managers.Consumers;
using OrderFlow.Orders.API.Managers.Data;
using OrderFlow.Orders.API.Managers.DataContext;
using OrderFlow.Orders.API.Managers.Facades;
using OrderFlow.Orders.API.Managers.Saga;
using Scalar.AspNetCore;

const string WebCorsPolicy = "WebCors";

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery, HTTP resilience. Also the Service Bus
// ActivitySource, without which the order's trace stops dead at the first publish.
builder.AddServiceDefaults();

// ── Infrastructure ──────────────────────────────────────────────────────────────────────────
// These come first so that every connection is registered before the consumers below, which
// start pulling messages the moment the host starts ([R]2). Each extension carries a setting
// that is silently fatal if dropped — above all the Cosmos camelCase serializer, without which
// every append fails on a partition-key mismatch. That is exactly why they are extensions and
// not three loose Aspire calls here.
builder.AddOrderEventStore();     // Cosmos container "order-events", partitioned by /orderId
builder.AddOrderReadModel();      // Redis "redis"
builder.AddOrderFlowMessaging();  // Service Bus "servicebus" + the idempotency store

// ── The onion ───────────────────────────────────────────────────────────────────────────────
// Controller → Facade → Business → (event store | read model | bus), with the saga alongside.
builder.Services.AddScoped<IOrderBusinessManager, OrderBusinessManager>();
builder.Services.AddScoped<IOrderFacade, OrderFacade>();
builder.Services.AddScoped<IOrderSaga, OrderSaga>();

// Six hosted processors, one per event the saga reacts to. Registered last: they are the only
// thing here that starts doing work on its own.
builder.AddOrderConsumers();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    // Named origins only. AllowAnyOrigin would let any page on the internet drive this API,
    // and with AllowCredentials it is not even legal ([R]1).
    options.AddPolicy(WebCorsPolicy, policy => policy
        .WithOrigins(builder.Configuration["WebOrigin"] ?? "http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

// /health and /alive — Development only, enforced inside MapDefaultEndpoints.
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    // [R]3. The OpenAPI document describes every route and shape in the service; it is not
    // something to serve in production by accident.
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors(WebCorsPolicy);

app.MapControllers();

app.Run();
