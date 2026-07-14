using OrderFlow.Notification.API.Managers.Business;
using OrderFlow.Notification.API.Managers.Consumers;
using OrderFlow.Notification.API.Managers.Data;
using OrderFlow.Notification.API.Managers.Facades;
using Scalar.AspNetCore;

const string WebCorsPolicy = "WebCors";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ── Infrastructure ──────────────────────────────────────────────────────────────────────────────
// Service Bus only. Notification stores nothing durable, on purpose — nothing reads its records back
// to make a decision, so it has no state worth protecting.
builder.AddOrderFlowMessaging();

// ── The onion ───────────────────────────────────────────────────────────────────────────────────
builder.AddNotificationStore();     // bounded in-memory ring
builder.AddNotificationBusiness();  // bounded retry + per-attempt timeout + the simulated provider
builder.AddNotificationFacade();

// Three hosted processors, all on the "notification" subscription: order-confirmed, order-failed,
// payment-declined. Registered last.
builder.AddNotificationConsumers();

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
