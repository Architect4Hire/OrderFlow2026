using OrderFlow.Payments.API.Managers.Business;
using OrderFlow.Payments.API.Managers.Consumers;
using OrderFlow.Payments.API.Managers.Data;
using OrderFlow.Payments.API.Managers.DataContext;
using OrderFlow.Payments.API.Managers.Facades;
using Scalar.AspNetCore;

const string WebCorsPolicy = "WebCors";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ── Infrastructure ──────────────────────────────────────────────────────────────────────────────
// Registered before the consumers, which start pulling the moment the host starts. SQL and Service
// Bus only — Payment has no business knowing that Cosmos or Redis exist.
builder.AddPaymentDataContext();  // SQL "PaymentDb"
builder.AddOrderFlowMessaging();  // Service Bus "servicebus" + the idempotency store

// ── The onion ───────────────────────────────────────────────────────────────────────────────────
builder.AddPaymentData();
builder.AddPaymentBusiness();     // also binds PaymentOptions and the simulated authorizer
builder.AddPaymentFacade();

// Two hosted processors: charge-payment and refund-payment. Registered last.
builder.AddPaymentConsumers();

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

// Schema BEFORE Run(). Hosted services start inside Run(), so this is what guarantees the
// charge-payment consumer cannot take its first message against a database that has no Payments
// table — and, more to the point, no unique index on IdempotencyKey.
await PaymentDbInitializer.InitializeAsync(app.Services);

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors(WebCorsPolicy);

app.MapControllers();

app.Run();
