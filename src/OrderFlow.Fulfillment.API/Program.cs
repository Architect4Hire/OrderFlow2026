using OrderFlow.Fulfillment.API.Managers.Business;
using OrderFlow.Fulfillment.API.Managers.Consumers;
using OrderFlow.Fulfillment.API.Managers.Facades;
using OrderFlow.ServiceDefaults.Messaging;
using Scalar.AspNetCore;

const string WebCorsPolicy = "WebCors";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ── Infrastructure ──────────────────────────────────────────────────────────────────────────────
// Service Bus only. Fulfillment holds no state of its own — no SQL, no Cosmos, no Redis. Its only
// durable record is the broker's dead-letter queue, which is what the ops endpoint reads.
builder.AddOrderFlowMessaging();  // Service Bus "servicebus" + the idempotency store

// ── The onion ───────────────────────────────────────────────────────────────────────────────────
builder.AddCarrierClient();       // Polly pipeline + the simulator, tuned for this dependency
builder.AddDeadLetterBrowser();   // shared with every other service
builder.AddFulfillmentBusiness();
builder.AddFulfillmentFacade();

// One hosted processor: dispatch-fulfillment. Registered last.
builder.AddFulfillmentConsumers();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    // Named origins only. Never a wildcard.
    options.AddPolicy(WebCorsPolicy, policy => policy
        .WithOrigins(builder.Configuration["WebOrigin"] ?? "http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors(WebCorsPolicy);

app.MapControllers();

app.Run();
