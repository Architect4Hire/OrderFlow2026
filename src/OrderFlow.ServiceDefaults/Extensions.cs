using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Telemetry, health, service discovery, and HTTP-client policy shared by every
/// OrderFlow API. Referenced by all five services so none of them can drift.
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// ActivitySource the Azure Service Bus SDK publishes its send/receive/process spans on.
    /// Registering it is what stitches message hops into the SAME distributed trace as the
    /// HTTP spans — without it, an order's trace stops dead at the first publish.
    /// </summary>
    private const string ServiceBusActivitySource = "Azure.Messaging.ServiceBus";

    /// <summary>
    /// The Azure SDK only emits ActivitySource spans when this switch is on. It is opt-in,
    /// and forgetting it is the single most common reason an Aspire messaging trace comes
    /// back empty. Setting it when already-enabled is a harmless no-op.
    /// </summary>
    private const string AzureActivitySourceSwitch = "Azure.Experimental.EnableActivitySource";

    /// <summary>
    /// Wires OpenTelemetry, default health checks, service discovery, and standard HTTP
    /// client resilience. Call from every API's Program.cs.
    /// </summary>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Configures metrics, tracing, and logging — including the Service Bus spans that make
    /// one order traceable end-to-end across all five services.
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        AppContext.SetSwitch(AzureActivitySourceSwitch, true);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource(ServiceBusActivitySource)
                    .AddAspNetCoreInstrumentation(aspNetCore =>
                        // Health probes fire constantly and would drown the order traces.
                        aspNetCore.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Registers a "self" liveness check tagged "live".</summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps /health (readiness — every check must pass) and /alive (liveness — only the
    /// "live"-tagged checks). Development only: these endpoints are unauthenticated.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath);

            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
