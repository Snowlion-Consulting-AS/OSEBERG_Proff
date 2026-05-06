# Change log

Detailed log of code changes made by Snowlion Consulting on the OSEBERG_Proff
codebase. Newest entries first. Each entry records: what changed, why, which
files / line ranges were touched, the expected impact, and verification steps.

The aim is that anyone reading this file (Snowlion, Oseberg, or a future
maintainer) can understand the intent of every change without reading the diff
in isolation.

---

## 2026-05-06 — Phase 1: Stop the bleed (frontend)

**Branch:** `fix/reduce-api-volume`
**Trigger:** Oseberg reported ~17,000 calls/day to the Proff lookup API against
an expected baseline of ~100 new accounts/day. Codebase audit identified four
amplification causes (two backend, two frontend). This entry covers the two
frontend fixes; the backend cache work is tracked as a separate entry once
applied.

**Pre-requisite (not yet completed):** Application Insights audit on the live
Function App `osb-proff-company-lookup` to confirm volume comes from PCF users
and not from a leaked/abused function key. The function key currently ships in
the PCF bundle (`VirtualControl/VirtualControl/config/config.ts:4`) and is
committed in git history — needs separate rotation regardless of the outcome
of this work.

### FE-1 — Remove duplicate enrichment call in `handleConfirm`

**File:** `VirtualControl/VirtualControl/Components/Search.tsx`
**Lines (before):** 226–246

**Problem:** When a user picks a search result that overwrites an existing
account, two API calls were fired for the *same* selection:

1. `setSelectedItem(cachedItem)` triggered the existing
   `useEffect([selectedItem])` on lines 101–124, which calls
   `handleSearch("", orgNr, proffCompanyId)` — the correct path.
2. The same `handleConfirm` function then *also* called `handleSearch(...)`
   directly using `selectedItem`, which still holds the **previous** value
   (because React state updates are async). This produced a second enrichment
   call against stale data and a stale `onCardClick` write-back.

**Fix:** Removed the duplicate `handleSearch` and `onCardClick` calls inside
`handleConfirm`. The `[selectedItem]` effect now owns enrichment exclusively.
`handleConfirm` is reduced to: set the new selected item, close the dialog,
clear the cache, and clear the search box.

**Expected impact:** Removes one full Proff round-trip per overwrite-confirmed
card click. Also removes a class of stale-data bugs where the wrong company's
detail data was being written to the form.

**Risk:** Low. The flow that produces correct data (the `useEffect`) is
unchanged.

### FE-2 — Cancel superseded fetches with `AbortController`

**File:** `VirtualControl/VirtualControl/Components/Search.tsx`

**Problem:** Each keystroke pause >1s triggered a new `fetch` to the Function
App. Previous in-flight requests were never cancelled — they completed,
billed Proff, and were silently discarded by `setData(...)` being overwritten.

**Fix:** Added a `useRef<AbortController>` and abort the previous controller at
the start of every `handleSearch`. The fetch now passes `signal` so the
underlying network request is cancelled. `AbortError` is suppressed — it's
expected, not a real error. A final `useEffect` cleanup aborts any in-flight
request when the component unmounts (form close / navigation).

**Expected impact:** Eliminates wasted fetches during fast typing or rapid
re-searches. Combined with FE-1, expected ≥30% reduction on the frontend
side alone, before any backend cache is added.

**Risk:** Low. Worst case: a user types, then immediately clicks a card before
the search response comes back — the search is cancelled and the card-click
enrichment proceeds normally (own request).

### Verification (manual, before deploy)

1. Build the PCF bundle (`cd VirtualControl && npm run build` or
   `pac pcf push` against a sandbox environment).
2. Open an Account form with the lookup PCF.
3. **Type a name slowly and watch DevTools → Network:**
   confirm only one request fires after the 1s pause, not one per keystroke
   pause within a continuous typing burst.
4. **Type "Bano", continue typing "Bano AS" within 1s:**
   confirm the first request appears as `(canceled)` in DevTools.
5. **Pick a result on a form that already has name + org-nr filled,
   confirm the dialog, accept the overwrite:**
   confirm exactly one detail-enrichment request fires (look for the URL with
   `&organisationNumber=` populated), not two.
6. **Open the form, type a search, close the form before the response
   arrives:** confirm the request appears as `(canceled)`, not completed.

### Files touched

- `VirtualControl/VirtualControl/Components/Search.tsx`

### Files NOT touched (intentionally, deferred)

- `VirtualControl/VirtualControl/config/config.ts` — function key rotation is
  a separate workstream and requires Oseberg's coordination.
- Country-aware org-number length, debounce tuning, conditional enrichment —
  Phase 2 (improvements), not stop-the-bleed.

---

## 2026-05-06 — Phase 1: Stop the bleed (backend)

**Branch:** `fix/reduce-api-volume` (same branch as the frontend fixes)
**Trigger:** Same audit as the frontend entries above. The backend portion
of the four amplification causes was: (1) no caching at all on the main
company-lookup endpoint (every call is a 1:1 pass-through to Proff), and
(2) the existing premium-credit-rating cache reads but never writes —
so it never populates and is effectively dead code.

### BE-1 — In-process IMemoryCache for company search and enrichment

**Files:**
- `LIVE-ProffCompanyLookup/Program.cs`
- `LIVE-ProffCompanyLookup/ProffCompanyLookup.cs`

**Problem:** `ProffCompanyLookup.Run` had no caching whatsoever. Every
identical search (same country + same query) and every identical detail
enrichment (same country + same orgnr) round-tripped to Proff. In a busy
office where multiple users search for the same handful of customers per
day, this is the single largest contributor to the daily call count.

**Fix:**
- Added `services.AddMemoryCache()` to the host configuration in
  `Program.cs`. Uses `Microsoft.Extensions.Caching.Memory` which is
  already available transitively via `Microsoft.AspNetCore.App` — no
  new package reference required.
- Injected `IMemoryCache` into `ProffCompanyLookup` via the constructor.
- Wrapped `GetCompanyData(...)` and `_proffApiService.GetDetailedCompanyInfoCopy(...)`
  in `_cache.GetOrCreateAsync(...)` calls, with a 15-minute absolute
  expiry. Cache keys: `search|{country}|{query}` and
  `enrich|{country}|{orgnr}`.
- Activity counter (`UpdateRequestCountAsync`) is intentionally still
  incremented on cache hits — it tracks tenant lookup activity for
  billing/quota purposes, not raw Proff calls. Proff-call savings will
  be visible separately on Proff's dashboards and in the Function App's
  outbound traffic metrics.

**Expected impact:** Largest single lever in the Phase 1 set. In a
realistic usage pattern where a small team repeatedly looks up the same
customer accounts, expect cache-hit rates of 50–80% within a 15-minute
window after warm-up.

**Caveats / known limitations (Phase 2 follow-ups):**
- `IMemoryCache` is per-worker. On a Consumption / Premium plan with
  multiple instances, each worker has its own cache and the effective
  hit rate is reduced. A distributed cache (Redis or Azure Table) would
  unify it across instances. Documented but not addressed here.
- 15-minute TTL is a guess. Tune after observing live hit-rate metrics.
- No negative-result discrimination — a 0-result Proff response is
  cached the same as a populated one. This is intentional (avoids
  re-asking Proff for typos) but worth revisiting if Proff data changes
  frequently.
- Cache is not bounded by entry count or memory size. For typical
  Oseberg traffic this is fine; revisit if memory pressure shows up.

**Risk:** Low. `IMemoryCache` failures fall through to direct calls
(default factory behavior). The existing pass-through behavior is
preserved on cache miss. Wrong-tenant data leakage is impossible — the
cache key does not include `domain`, but the cached *data* is
non-tenant-specific (it's a company record from a public registry).

### BE-1 verification (manual, before deploy)

1. Build the Function locally: `cd LIVE-ProffCompanyLookup && dotnet build`.
2. Deploy to a non-LIVE Function App if available (DEV / TEST). If not,
   coordinate with Oseberg before deploying to LIVE.
3. **Search cache:** call the function twice with identical
   `?country=NO&query=Bano&domain=...` within 15 minutes. The second
   call should respond noticeably faster (no Proff round-trip). Confirm
   in Application Insights `dependencies` table — only one outbound
   call to `api.proff.no` for the pair.
4. **Enrichment cache:** call twice with identical
   `?country=NO&organisationNumber=...&domain=...`. Same behavior.

### BE-2 — Persist successful premium fetches to the existing cache

**File:** `LIVE-ProffCompanyLookup/ProffPremiumLookup.cs`

**Problem:** `ProffPremiumLookup.Run` already calls
`_proffPremiumCacheService.GetPremiumInfoAsync(...)` to check the Azure
Table cache for credit ratings. On hit it returns the cached result. On
miss it fetches fresh from Proff Premium — **but never calls
`CreateOrUpdatePremiumInfoAsync`**, so the cache table was never
populated and every premium request was effectively uncached. The
cache check was dead code.

**Fix:** After a successful fresh fetch (`statusCode == OK` and
`creditRating != null`), call `CreateOrUpdatePremiumInfoAsync(orgNr,
country, creditRating)` so the next lookup for the same orgnr serves
from Azure Table.

**Expected impact:** Depends on the relative volume of premium credit-
rating calls vs. main company lookups. If premium is a meaningful slice
of the 17k/day, this single addition (with a status/null guard)
materially reduces Proff Premium round-trips. If premium is a tiny
slice, this is a hygiene fix that makes a latent bug unlatent.

**Caveats / known limitations (Phase 2 follow-ups):**
- Azure Table entries have no TTL. Credit ratings change over time, so
  an indefinite cache will eventually return stale data. A
  `last_updated` timestamp + age check on read is the proper fix.
- No invalidation hook — manual cache flush is the only recourse if
  bad data sneaks in.

**Risk:** Very low. Pure addition guarded by status / null check; the
response path is unchanged.

**BE-2 verification:**
1. Build: `cd LIVE-ProffCompanyLookup && dotnet build` (0 errors).
2. Call the premium endpoint with a fresh orgnr that is **not** in the
   `ProffPremiumCache` Azure Table. Expect status 200 + a row to appear
   in the table after the call (partitionKey = country, rowKey =
   orgnr).
3. Call the same endpoint again. Expect status 200 + no outbound
   dependency call to `ppc.proff.no` in App Insights for this request.
4. Call with an orgnr that returns a non-200 from Proff. Expect no
   row to be written to the cache table.

### Files touched

- `LIVE-ProffCompanyLookup/Program.cs`
- `LIVE-ProffCompanyLookup/ProffCompanyLookup.cs`
- `LIVE-ProffCompanyLookup/ProffPremiumLookup.cs`

### Files NOT touched (intentionally, deferred)

- `Services/API/ProffApiService.cs` — the static `HttpClient` with
  per-request header mutation is a known concurrency footgun, but
  fixing it is out of scope for stop-the-bleed (Phase 2 hardening).
- The two legacy backend folders (`ProffCompanyLookupService`,
  `ProffCompanyLookupService_OLD_DotNet6Version`) — deferred until
  confirmed unused with Oseberg.
- Activity counter semantics (whether to count cache hits separately) —
  flagged for Phase 2 once the billing model is confirmed.
