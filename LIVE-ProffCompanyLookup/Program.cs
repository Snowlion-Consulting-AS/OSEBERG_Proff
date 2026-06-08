using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // NOTE: App Insights worker registration temporarily removed while
        // diagnosing a .NET 10 / Worker 2.x startup crash on osb-proff-v2.
        // In-process IMemoryCache used by ProffCompanyLookup to dedupe
        // identical Proff lookups within a short window. See CHANGES.md
        // (BE-1) for context. On a multi-instance / consumption plan the
        // cache is per-worker, not shared — distributed cache (Redis or
        // Azure Table) is a Phase 2 follow-up.
        services.AddMemoryCache();
    })
    .Build();

host.Run();
