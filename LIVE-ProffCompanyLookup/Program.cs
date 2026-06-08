using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // .NET 10 isolated telemetry: send logs/traces to Application Insights
        // via OpenTelemetry + Azure Monitor exporter. Replaces the legacy
        // AddApplicationInsightsTelemetryWorkerService/ConfigureFunctionsApplicationInsights
        // pair, which crashes the Worker 2.x startup on net10. Requires
        // host.json "telemetryMode": "OpenTelemetry" and the
        // APPLICATIONINSIGHTS_CONNECTION_STRING app setting.
        services.AddOpenTelemetry()
            .UseFunctionsWorkerDefaults()
            .UseAzureMonitorExporter();
        // In-process IMemoryCache used by ProffCompanyLookup to dedupe
        // identical Proff lookups within a short window. See CHANGES.md
        // (BE-1) for context. On a multi-instance / consumption plan the
        // cache is per-worker, not shared — distributed cache (Redis or
        // Azure Table) is a Phase 2 follow-up.
        services.AddMemoryCache();
    })
    .Build();

host.Run();
