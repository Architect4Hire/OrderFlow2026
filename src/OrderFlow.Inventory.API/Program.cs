using OrderFlow.Inventory.API.Managers.Business;
using OrderFlow.Inventory.API.Managers.Consumers;
using OrderFlow.Inventory.API.Managers.Data;
using OrderFlow.Inventory.API.Managers.DataContext;
using OrderFlow.Inventory.API.Managers.Facades;
using Scalar.AspNetCore;

const string WebCorsPolicy = "WebCors";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ── Infrastructure ──────────────────────────────────────────────────────────────────────────────
// Registered before the consumers, which start pulling the moment the host starts. SQL and Service
// Bus only — Inventory has no business knowing that Cosmos or Redis exist.
builder.AddInventoryDataContext();  // SQL "InventoryDb"
builder.AddOrderFlowMessaging();    // Service Bus "servicebus" + the idempotency store

// ── The onion ───────────────────────────────────────────────────────────────────────────────────
builder.AddInventoryData();
builder.AddInventoryBusiness();
builder.AddInventoryFacade();

// Two hosted processors: reserve-inventory and release-inventory. Registered last.
builder.AddInventoryConsumers();

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

// Schema and seed stock, BEFORE Run(). Hosted services start inside Run(), so doing this here is
// what guarantees the reserve-inventory consumer cannot take its first message against a database
// that has no tables in it yet.
await InventoryDbInitializer.InitializeAsync(app.Services);

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors(WebCorsPolicy);

app.MapControllers();

app.Run();
