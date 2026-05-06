using System.Net;
using Proff.Infrastructure;
using Proff.Services;
using Proff.ExternalServices;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Proff.Models;
using LIVE_ProffCompanyLookup.Utils;

namespace Proff.Function
{
  public class ProffCompanyLookup
  {
    private const string AzureRequestTableActivityName = "ProffRequestActivity";
    private const string AzureConfigurationTableName = "ProffConfiguration";
    private const string HttpMessageMissingRequiredParameters = "Missing required parameters";
    private const string HttpMessageNoActiveSubscription = "No active subscription found";
    // Cache lifetime for both list-search and detail-enrichment results.
    // 15 minutes balances "respond instantly to repeated lookups across the
    // office" against "do not serve too-stale company data". Tunable.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly ILogger<ProffCompanyLookup> _logger;
    private readonly IMemoryCache _cache;
    private static HttpResponseData? _response;
    private AzureTableStorageService _azureRequestActivityService;
    private AzureTableStorageService _azureConfigurationService;
    private ProffActivityService _proffActivityService;
    private ProffApiService _proffApiService;

    public ProffCompanyLookup(ILogger<ProffCompanyLookup> logger, IMemoryCache cache)
    {
      _logger = logger;
      _cache = cache;
      _azureRequestActivityService = new AzureTableStorageService(AzureRequestTableActivityName);
      _azureConfigurationService = new AzureTableStorageService(AzureConfigurationTableName);
      _proffActivityService = new ProffActivityService(_azureRequestActivityService);
      _proffApiService = new ProffApiService(_azureConfigurationService);
    }

    [Function("ProffCompanyLookup")]
    public async Task<HttpResponseData> Run(
      [HttpTrigger(AuthorizationLevel.Function, "get")]
      HttpRequestData req, FunctionContext executionContext)
    {
      InputParams inputParams = new(req);

      if (!await _azureConfigurationService.EntityHasActiveSubscription(inputParams.domain))
      {
        return await HttpHelper.ConstructHttpResponse(_response, req, HttpStatusCode.BadRequest,
          HttpMessageNoActiveSubscription);
      }

      if (string.IsNullOrEmpty(inputParams.organisationNumber))
      {
        if (string.IsNullOrEmpty(inputParams.query) || string.IsNullOrEmpty(inputParams.country))
        {
          return await HttpHelper.ConstructHttpResponse(_response, req, HttpStatusCode.BadRequest,
            HttpMessageMissingRequiredParameters);
        }

        // Cache list-search results so identical lookups (same country +
        // same query) within CacheLifetime do not round-trip to Proff.
        // The activity counter is intentionally still incremented on cache
        // hits — it tracks tenant lookup activity for billing/quota, not
        // raw Proff calls. Proff-call savings are visible separately on
        // Proff's own dashboards and in the function's egress metrics.
        string searchCacheKey = $"search|{inputParams.country}|{inputParams.query}";
        var companies = await _cache.GetOrCreateAsync(searchCacheKey, async entry =>
        {
          entry.AbsoluteExpirationRelativeToNow = CacheLifetime;
          return await GetCompanyData(inputParams.query, inputParams.country);
        });
        await _proffActivityService.UpdateRequestCountAsync(inputParams.domain);
        return await HttpHelper.ConstructHttpResponse(_response, req, HttpStatusCode.OK, companies);
      }

      // Detail-enrichment cache: same orgnr returns the same record
      // regardless of which user is looking it up.
      string enrichmentCacheKey = $"enrich|{inputParams.country}|{inputParams.organisationNumber}";
      JObject extraCompanyInfo = await _cache.GetOrCreateAsync(enrichmentCacheKey, async entry =>
      {
        entry.AbsoluteExpirationRelativeToNow = CacheLifetime;
        return await _proffApiService.GetDetailedCompanyInfoCopy(inputParams.country, inputParams.organisationNumber);
      });
      await _proffActivityService.UpdateRequestCountAsync(inputParams.domain);
      return await HttpHelper.ConstructHttpResponse(_response, req, HttpStatusCode.OK, extraCompanyInfo);
    }

    private async Task<List<CompanyData>> GetCompanyData(string query, string country)
    {
      JArray companies = await _proffApiService.FetchCompanyDataAsync(query, country);
      CompanyDataService companyDataService = new();
      var companyDataList = companyDataService.ConvertJArrayToCompanyDataList(companies);
      return companyDataList;
    }
  }
}