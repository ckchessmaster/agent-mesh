using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string MetricsEndpointPath = "/metrics";

    /// <summary>
    /// Reads the opt-in Prometheus scrape endpoint settings (<c>Metrics:Prometheus:Enabled</c>
    /// and the optional <c>Metrics:Prometheus:Port</c>).
    /// </summary>
    /// <remarks>
    /// The port matters more than it first looks. A scrape endpoint cannot reasonably be
    /// authenticated (it would mean handing Prometheus a bearer token), and ASP.NET Core has
    /// no "same listener, different rules" concept - so serving <c>/metrics</c> on the API's own
    /// listener means anything that can reach the ingress can read it. A dedicated listener that
    /// the Kubernetes Service deliberately does not publish is reachable from the pod network,
    /// which is where the scraper lives, and unreachable from outside.
    ///
    /// A port of zero means "no extra listener": serve the endpoint on whatever the app already
    /// binds, which is the right shape for a local run.
    ///
    /// The port is *not* bound here on purpose - it has to appear in the app's listen URLs
    /// (<c>ASPNETCORE_URLS</c>), which is what the Helm chart does in one place. Binding it from
    /// code looks convenient and is a trap: adding a listener through
    /// <c>KestrelServerOptions.ListenAnyIP</c> makes Kestrel discard the <c>ASPNETCORE_URLS</c>
    /// binding wholesale, so the application's real port stops listening and every probe and
    /// Service endpoint dies with it. Leaving the port unbound is also the safe failure mode -
    /// <c>/metrics</c> is then simply unreachable, rather than suddenly reachable on the public
    /// listener.
    /// </remarks>
    private static (bool Enabled, int Port) GetPrometheusScrapeEndpoint(IConfiguration configuration)
    {
        var section = configuration.GetSection("Metrics:Prometheus");

        if (!bool.TryParse(section["Enabled"], out var enabled) || !enabled)
        {
            return (false, 0);
        }

        // A missing, unparsable or zero port is not a reason to drop metrics - it only means
        // "do not open a second listener".
        if (!int.TryParse(section["Port"], out var port) || port <= 0)
        {
            return (true, 0);
        }

        return (true, port);
    }

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
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
                    .AddRuntimeInstrumentation()
                    // Meters the defaults leave off, each one load-bearing for capacity and
                    // restart investigations: Kestrel reports connection and queue depth,
                    // SignalR reports hub connections (the API keys per-connection state in
                    // static dictionaries), and the EF Core / Npgsql meters show a growing
                    // query load or connection pool. An unregistered meter is a no-op, so
                    // naming one that a given build does not ship costs nothing.
                    .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                    .AddMeter("Microsoft.AspNetCore.SignalR")
                    .AddMeter("Microsoft.EntityFrameworkCore")
                    .AddMeter("Npgsql");

                if (GetPrometheusScrapeEndpoint(builder.Configuration).Enabled)
                {
                    // Additive next to the OTLP exporter rather than instead of it: Aspire keeps
                    // receiving pushes locally, and Prometheus scrapes the same MeterProvider.
                    metrics.AddPrometheusExporter();
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
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

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Expose health and aliveness probes for container orchestrators (Kubernetes / Docker)
        app.MapHealthChecks(HealthEndpointPath);

        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        var (enabled, port) = GetPrometheusScrapeEndpoint(app.Configuration);

        if (enabled)
        {
            if (port > 0)
            {
                // Middleware rather than an endpoint, because only the middleware overload can
                // gate on the local port: a scrape that arrives on the application's own listener
                // has to 404 instead of quietly succeeding there.
                app.UseOpenTelemetryPrometheusScrapingEndpoint(context =>
                    context.Connection.LocalPort == port
                    && context.Request.Path.StartsWithSegments(MetricsEndpointPath, StringComparison.OrdinalIgnoreCase));

                // And the reverse: this listener exists only to be scraped, so it serves nothing
                // else. Without that, the extra port would answer every route the application has,
                // turning an intentionally unpublished listener into a side door the moment anyone
                // adds it to a Service or an Ingress.
                app.Use(async (context, next) =>
                {
                    if (context.Connection.LocalPort == port)
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next();
                });
            }
            else
            {
                app.MapPrometheusScrapingEndpoint(MetricsEndpointPath);
            }
        }

        return app;
    }
}
