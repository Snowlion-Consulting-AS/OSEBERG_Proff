# OSEBERG Proff – integrasjon mot Proff for Dynamics 365

Berikelse av forretningsforbindelser, emner og kontakter i Dynamics 365 CRM med
firmadata og kredittvurdering fra [Proff](https://www.proff.no/). Løsningen består av
en **PCF-kontroll** (frontend, React + Fluent UI) og en **Azure Function** (backend,
.NET 10 isolated worker) som mellomledd mot Proff sine REST-tjenester, med caching,
abonnementskontroll og forbruksregistrering i Azure Table Storage.

> Utviklet og vedlikeholdt av **Snowlion Consulting AS** for Oseberg Solutions.

---

## Innhold

1. [Oversikt](#oversikt)
2. [Arkitektur](#arkitektur)
3. [Repostruktur](#repostruktur)
4. [Frontend – PCF-kontroll (VirtualControl)](#frontend--pcf-kontroll-virtualcontrol)
5. [Backend – LIVE-ProffCompanyLookup (.NET 10)](#backend--live-proffcompanylookup-net-10)
6. [Azure-ressurser](#azure-ressurser)
7. [Azure Table Storage](#azure-table-storage)
8. [Proff API-integrasjon](#proff-api-integrasjon)
9. [Web-ressurser (gauges)](#web-ressurser-gauges)
10. [Konfigurasjon og hemmeligheter](#konfigurasjon-og-hemmeligheter)
11. [Observability / telemetri](#observability--telemetri)
12. [Lokal utvikling](#lokal-utvikling)
13. [Bygg og deploy](#bygg-og-deploy)
14. [Sikkerhet](#sikkerhet)
15. [Kjente begrensninger og videre arbeid](#kjente-begrensninger-og-videre-arbeid)
16. [Eldre prosjekter (legacy)](#eldre-prosjekter-legacy)
17. [Dokumentasjon og endringslogg](#dokumentasjon-og-endringslogg)

---

## Oversikt

Når en bruker oppretter eller redigerer en forretningsforbindelse/emne i Dynamics 365,
kan de søke opp et firma via en innebygget søkekontroll. Kontrollen kaller en Azure
Function, som igjen henter data fra Proff og returnerer treff. Når brukeren velger et
firma, fylles relevante felter på skjemaet automatisk (navn, organisasjonsnummer,
adresse, nøkkeltall m.m.). For kunder med premium-lisens kan også kredittvurdering
hentes og vises som måleinstrument (gauge).

**Hvorfor en Azure Function i midten?** Proff tillater ikke direkte kall fra nettleser
til sitt API (CORS / autentisering). Azure Functionen fungerer som en sikker proxy som
holder API-nøklene, håndterer caching for å redusere antall (betalte) kall mot Proff,
kontrollerer hvilke kunder som har aktivt abonnement, og logger forbruk per kunde.

Hovedevner:

- **Firmasøk** på navn eller bransje (NACE), per land (NO/SE/DK).
- **Berikelse** av enkeltselskap via organisasjonsnummer (adresse, nøkkeltall, økonomi).
- **Kredittvurdering** (premium) med score og rating.
- **Abonnementskontroll** per CRM-domene.
- **Caching** (in-memory + Azure Table) for å spare Proff-kall.
- **Forbruksregistrering** per kunde og måned (grunnlag for fakturering).

---

## Arkitektur

```
        Dynamics 365 CRM (modellbasert app)
        ┌───────────────────────────────────────┐
        │  Skjema: Forretningsforbindelse/Emne   │
        │  ┌─────────────────────────────────┐   │
        │  │ PCF-kontroll «VirtualControl»    │   │   Web-ressurser (HTML/JS):
        │  │ (React + Fluent UI)              │   │   • ProffRating.html (gauge)
        │  └───────────────┬─────────────────┘   │   • keynumbers_account/lead.html
        └──────────────────┼─────────────────────┘
                           │ HTTPS (function key i URL)
                           ▼
        ┌───────────────────────────────────────┐
        │  Azure Function (.NET 10 isolated)     │
        │  • ProffCompanyLookup   (søk/berik)    │
        │  • ProffPremiumLookup   (kreditt)      │
        │                                        │
        │  In-memory cache (15 min)              │
        └───┬───────────────────────────┬────────┘
            │                           │
            ▼                           ▼
   ┌──────────────────┐      ┌─────────────────────────┐
   │  Proff API        │      │  Azure Table Storage     │
   │  • api.proff.no   │      │  • ProffConfiguration    │
   │  • ppc.proff.no   │      │  • ProffRequestActivity  │
   │   (premium)       │      │  • ProffPremiumCache     │
   └──────────────────┘      │  • ProffPremiumRequest…  │
                             └─────────────────────────┘

   Hemmeligheter: Azure Key Vault (kv-osb-proff) → app settings som KeyVault-referanser
   Telemetri:     OpenTelemetry → Azure Monitor / Application Insights
```

**Dataflyt (firmasøk):**

1. Bruker skriver i søkefeltet → PCF debouncer (1 sek, min. 3 tegn) og kaller `ProffCompanyLookup`.
2. Functionen sjekker at CRM-domenet har aktivt abonnement (`ProffConfiguration`).
3. Treff hentes fra Proff (eller fra in-memory cache), forbruk logges (`ProffRequestActivity`).
4. Brukeren velger et firma → PCF gjør et nytt, berikende kall med organisasjonsnummer.
5. Feltene på CRM-skjemaet fylles via `getOutputs()`.

---

## Repostruktur

```
OSEBERG_Proff/
├── LIVE-ProffCompanyLookup/          # AKTIV backend – Azure Function (.NET 10 isolated)
│   ├── ProffCompanyLookup.cs         #   HTTP-funksjon: firmasøk + berikelse
│   ├── ProffPremiumLookup.cs         #   HTTP-funksjon: kredittvurdering (premium)
│   ├── Program.cs                    #   Host-oppsett, DI, OpenTelemetry
│   ├── host.json                     #   telemetryMode = OpenTelemetry
│   ├── Models/                       #   Company, CreditRating, InputParams
│   ├── Services/                     #   Proff-API-klienter, aktivitet, cache
│   ├── Infrastructure/               #   AzureTableStorageService
│   └── Utils/                        #   HttpHelper, ValidationUtils
│
├── VirtualControl/                   # AKTIV frontend – PCF-kontroll
│   ├── VirtualControl/               #   Kildekode (index.ts, Components/, config/ …)
│   ├── Solutions/                    #   Dataverse-løsning (publisher «OSEBERG», prefiks «os»)
│   └── package.json                  #   PCF build-scripts
│
├── htmlResource/                     # D365 web-ressurser (gauges/visualisering)
│   ├── ProffRating.html              #   Kredittvurdering som måleinstrument
│   ├── keynumbers_account.html       #   Nøkkeltall (likviditet/lønnsomhet/soliditet)
│   ├── keynumbers_lead.html
│   └── GaugeLibrary/dist/gauge.min.js
│
├── docs/                             # Teknisk dokumentasjon (HTML) + arkiv
│   ├── codebase-overview.html
│   ├── improvement-roadmap.html
│   ├── billing-pipeline-plan.html
│   └── archive/README.original.md    #   Opprinnelig (utdatert) README
│
├── ProffCompanyLookupService/                  # LEGACY – tom prosjektskall (utdatert)
├── ProffCompanyLookupService_OLD_DotNet6Version/ # LEGACY – .NET 6-versjon (utdatert)
├── CHANGES.md                        # Endringslogg (BE-/FE-/Q-IDer)
└── .vscode/                          # Build- og deploy-oppgaver for backend
```

---

## Frontend – PCF-kontroll (VirtualControl)

En **virtuell PCF-kontroll** (Power Apps Component Framework) skrevet i TypeScript med
React 16.8.6 og Fluent UI 8.29.0 (begge som `platform-library`). Plasseres på et
søkefelt på skjemaet for forretningsforbindelse/emne.

| Egenskap | Verdi |
|---|---|
| Namespace | `CompanyLookup` |
| Constructor | `VirtualControl` |
| Versjon | `1.4.0` |
| Type | `virtual` |
| Platform-libs | React `16.8.6`, Fluent UI `8.29.0` |
| Publisher / prefiks | `OSEBERG` / `os` |

> ⚠️ **React-tak:** `platform-library` aksepterer kun React **16.8.6** eller **17.0.2**.
> Ikke oppgrader til React 18+ for denne kontrollen.

### Komponenter

- `index.ts` – kontrollklassen (`init`/`updateView`/`getOutputs`/`destroy`). Holder
  intern state for ~22 datafelt og skriver dem tilbake til skjemaet i `getOutputs()`.
  Spesialhåndtering: ansatt-intervall «1-4» normaliseres til «4», og bransjekode mappes
  til både Account (`os_nace`) og Lead (`sic`).
- `Components/Search.tsx` – hovedkomponenten: Fluent `SearchBox`, landvelger (NO/SE/DK
  med flagg), debouncing, validering av org.nr (min. 9 sifre), kall mot Azure Function,
  `AbortController` for å kansellere utdaterte søk, og bekreftelsesdialog ved
  overskriving av eksisterende data.
- `Components/Flags/*`, `FlagRenderer.tsx`, `FlagOption.tsx` – landflagg (SVG).
- `interfaces/CompanyData.ts` – datamodell for treff.
- `config/config.ts` – `AZURE_FUNCTION_BASE_URL` + `AZURE_FUNCTION_API_KEY`.
- `constants/`, `utils/`, `css/` – land-alternativer, hjelpefunksjoner og stiler.

### Feltmapping

Kontrollen er databundet mot skjemafelter via `ControlManifest.Input.xml`. Standardfelter
mappes automatisk; ikke-standard felter (f.eks. NACE, nøkkeltall) må mappes manuelt i
egenskapene til kontrollen på skjemaet. Se [arkivert README](docs/archive/README.original.md)
for en bildebasert gjennomgang av mappingen i skjemadesigneren.

### Endepunkt-konfigurasjon

`config/config.ts` peker i dag mot den **gamle** function-appen
(`osb-proff-company-lookup`). Migrering av frontend til den nye .NET 10-appen
(`osb-proff-v2`) er planlagt som eget steg – se
[Kjente begrensninger](#kjente-begrensninger-og-videre-arbeid).

---

## Backend – LIVE-ProffCompanyLookup (.NET 10)

Azure Function på **.NET 10 isolated worker model** (Functions runtime v4), ASP.NET Core-
integrasjon (`ConfigureFunctionsWebApplication`).

### Funksjoner

#### `ProffCompanyLookup` — firmasøk og berikelse
- **Trigger:** HTTP `GET`, `AuthorizationLevel.Function`
- **Parametere:**
  - `domain` (påkrevd) – CRM-domenet (abonnementskontroll)
  - `query` + `country` – søk på navn/bransje, eller
  - `organisationNumber` + `country` – berikelse av ett selskap
- **Flyt:** abonnementssjekk → cache-oppslag (15 min) → Proff-kall ved miss →
  forbruk logges → JSON-respons (liste med `CompanyData` ved søk, objekt ved berikelse)
- **Feil:** `400` ved manglende parametere eller manglende aktivt abonnement

#### `ProffPremiumLookup` — kredittvurdering
- **Trigger:** HTTP `GET`/`POST`, `AuthorizationLevel.Function`
- **Parametere:** `domain`, `organisationNumber`, `country` (alle påkrevd)
- **Flyt:** premium-lisenssjekk → `ProffPremiumCache`-oppslag → Proff Premium-kall ved
  miss → skriv til cache → forbruk logges (`ProffPremiumRequestActivity`)
- **Respons:** JSON `CreditRating`

### Tjenester og infrastruktur

| Klasse | Ansvar |
|---|---|
| `Services/API/ProffApiService` | Kaller `https://api.proff.no/api` (søk + detaljer). Auth via `PROFF_API_KEY`. |
| `Services/API/ProffPremiumApiService` | Kaller `https://ppc.proff.no` (kredittscore). Auth via `PROFF_PREMIUM_API_TOKEN`. |
| `Services/CompanyDataService` | Mapper Proff-JSON (`JArray`) til `CompanyData`. |
| `Services/ProffActivityService` | Teller kall per domene/måned i `ProffRequestActivity`. |
| `Services/ProffPremiumActivityService` | Teller premium-kall per org.nr/måned. |
| `Services/ProffPremiumCacheService` | Les/skriv kredittvurdering i `ProffPremiumCache`. |
| `Infrastructure/AzureTableStorageService` | Generisk Table Storage-klient + abonnements-/lisenssjekk. |
| `Utils/HttpHelper` | Bygger HTTP-respons med JSON-serialisering. |
| `Utils/ValidationUtils` | Validering av input (delvis erstattet av `InputParams`). |

### Modeller (`Models/`)

- `InputParams` – parsing/validering av spørringsparametere (org.nr renses for whitespace).
- `Company` (`CompanyData`) – firmadata: navn, org.nr, e-post, adresser, ansatte, NACE,
  økonomiske nøkkeltall (profit, revenue, likviditetsgrad, totalrentabilitet, egenkapitalandel).
- `CreditRating` – rating, score, økonomikomponenter, oppdateringsdato.

### Caching-strategi

- **In-memory (`IMemoryCache`)** i functionen: 15 min TTL for både søk og berikelse.
  Per-instans (ikke delt mellom instanser på forbruksplan).
- **Azure Table (`ProffPremiumCache`)** for kredittvurderinger – persistent, men uten TTL i dag.

---

## Azure-ressurser

Alt ligger i subscription **«Oseberg Drift»**, ressursgruppe **`dev-crm-rg`**, region
**Norway East**.

| Ressurs | Navn | Merknad |
|---|---|---|
| Function App (ny, aktiv) | **`osb-proff-v2`** | .NET 10 isolated, **Flex Consumption** (`dotnet-isolated 10.0`). |
| Function App (gammel) | `OSB-proff-company-lookup` | .NET 8. Kjører fortsatt parallelt; frontend peker hit inntil cutover. |
| Function App (test) | `osb-test` | Testmiljø. |
| Function App (annet) | `Company-Lookup` | — |
| Key Vault | `kv-osb-proff` | RBAC-modus. Inneholder Proff-nøklene. |
| Storage Account | `proffwebstorage` | Holder alle Table Storage-tabellene. |

> 🔒 Tilgang til «Oseberg Drift» behandles som **read-only** for daglig arbeid
> (`list`/`show`/`get`). Skriveoperasjoner (deploy, app settings) gjøres bevisst og
> begrenset. Standard-subscription skal ikke endres.

---

## Azure Table Storage

Tabeller i storage-kontoen `proffwebstorage`:

| Tabell | Formål | Nøkler / kolonner |
|---|---|---|
| **ProffConfiguration** | Abonnement og lisens per CRM-domene. **Må ha én rad per kunde** for at tjenesten skal fungere. | PartitionKey/RowKey = domene. Kolonner: `domain`, `active_subscription` (bool), `premium_subscription` (bool). |
| **ProffRequestActivity** | Forbruk per domene/måned (faktureringsgrunnlag). | RowKey = `{domene}_{ÅÅÅÅMM}`. Kolonner: `domain`, `amount_of_request`, `last_request`, `year`, `month`. |
| **ProffPremiumCache** | Cache av kredittvurderinger for å spare premium-kall. | PartitionKey = land, RowKey = org.nr. Kolonner: `rating`, `ratingScore`, `economy`, `leadOwnership`, `organisationNumber`. |
| **ProffPremiumRequestActivity** | Forbruk av premium-oppslag per org.nr/måned. | RowKey = `{org.nr}_{ÅÅÅÅMM}`. Kolonner: `amount_of_request`, `last_request`. |

**Aktivere en ny kunde:** legg til en rad i `ProffConfiguration` med
`PartitionKey` = `RowKey` = kundens CRM-domene, `active_subscription = true`, og
`premium_subscription = true/false`.

---

## Proff API-integrasjon

| Tjeneste | Base-URL | Nøkkel | Bruk |
|---|---|---|---|
| Proff API (basis) | `https://api.proff.no/api` | `PROFF_API_KEY` | Firmasøk og berikelse (adresse, nøkkeltall, økonomi). |
| Proff Premium | `https://ppc.proff.no` | `PROFF_PREMIUM_API_TOKEN` | Kredittvurdering (rating + score). |

**Basisfelter** (kan være tomme): organisasjonsnummer, NACE, antall ansatte, besøks-/
postadresse, hjemmeside, profit, likviditetsgrad, totalrentabilitet, egenkapitalandel,
omsetning. **Premium** legger til: economy, leadOwnership, rating, ratingScore.

---

## Web-ressurser (gauges)

HTML/JS-ressurser som lastes opp som D365 web resources og vises på skjema:

- **`ProffRating.html`** – viser kredittvurdering som måleinstrument (rød/oransje/grønn
  sone, 0–100). Henter org.nr fra account-feltet (`cr41c_orgnr`) via Xrm-API og kaller
  `ProffPremiumLookup`.
- **`keynumbers_account.html` / `keynumbers_lead.html`** – tre gauges for likviditet,
  lønnsomhet og soliditet basert på nøkkeltall fra Proff.
- **`GaugeLibrary/dist/gauge.min.js`** – delt JS-bibliotek for selve gauge-tegningen
  (publiseres som web resource `os_gauge.min.js`).

> ⚠️ `ProffRating.html` inneholder i dag en **hardkodet function key** og peker mot den
> gamle appen. Se [Sikkerhet](#sikkerhet).

---

## Konfigurasjon og hemmeligheter

App settings på function-appen (`osb-proff-v2`):

| Setting | Kilde | Merknad |
|---|---|---|
| `PROFF_API_KEY` | **Key Vault-referanse** → `kv-osb-proff` / `osb-proff-api-key` | `@Microsoft.KeyVault(VaultName=kv-osb-proff;SecretName=osb-proff-api-key)` |
| `PROFF_PREMIUM_API_TOKEN` | **Key Vault-referanse** → `kv-osb-proff` / `osb-proff-premium-api-key` | — |
| `AZURE_PROFF_WEBSTORAGE_CONNECTIONSTRING` | Vanlig app setting (env var) | Tilkobling til `proffwebstorage`. **Skal aldri ligge i git.** |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Vanlig app setting | Driver telemetri-eksporten. |

Key Vault-referanser løses av function-appens **system-tilordnede managed identity**, som
må ha rollen **`Key Vault Secrets User`** på `kv-osb-proff`. Koden leser verdiene som
vanlige miljøvariabler (`Environment.GetEnvironmentVariable`) – referansene løses
transparent i plattformen.

> 🔒 **Ingen hemmeligheter i git.** `local.settings.json` og andre filer med hemmeligheter
> er git-ignorert (se rot-`.gitignore`). Verdier finnes kun som app settings / Key Vault.

---

## Observability / telemetri

På .NET 10 isolated gjøres Application Insights via **OpenTelemetry**, ikke de gamle
worker-pakkene (de krasjer workeren ved oppstart på net10 / Worker 2.x).

- Pakker: `Microsoft.Azure.Functions.Worker.OpenTelemetry`, `Azure.Monitor.OpenTelemetry.Exporter`
- `Program.cs`: `services.AddOpenTelemetry().UseFunctionsWorkerDefaults().UseAzureMonitorExporter();`
- `host.json`: `"telemetryMode": "OpenTelemetry"`
- Eksport styres av `APPLICATIONINSIGHTS_CONNECTION_STRING`.

---

## Lokal utvikling

**Forutsetninger:** .NET 10 SDK, Azure Functions Core Tools v4, Node.js + npm (for PCF),
Power Platform CLI (`pac`), og (for PCF på Windows) MSBuild fra Visual Studio Build Tools.

### Backend

```bash
cd LIVE-ProffCompanyLookup
# Opprett local.settings.json (git-ignorert) med:
#   PROFF_API_KEY, PROFF_PREMIUM_API_TOKEN,
#   AZURE_PROFF_WEBSTORAGE_CONNECTIONSTRING, FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
dotnet build
func start            # kjører funksjonene lokalt
```

### Frontend (PCF)

```bash
cd VirtualControl
npm install
npm start             # lokal test-harness (pcf-scripts start)
```

---

## Bygg og deploy

### Backend → `osb-proff-v2` (Flex Consumption)

```bash
cd LIVE-ProffCompanyLookup
dotnet publish -c Release -o bin/Release/net10.0/publish

# Pakk publish-output og deploy via Flex «One Deploy»
cd bin/Release/net10.0/publish && zip -r ../osb-proff-v2.zip .
az functionapp deployment source config-zip \
  -g dev-crm-rg -n osb-proff-v2 --subscription "Oseberg Drift" \
  --src ../osb-proff-v2.zip
```

> ℹ️ `.vscode/settings.json` peker fortsatt mot `bin/Release/net8.0/publish` fra før
> migreringen. Oppdater til `net10.0` før VS Code-deploy brukes, eller bruk `az`-kommandoen over.

### Frontend (PCF) → Dataverse

```bash
cd VirtualControl
msbuild /t:build /restore
pac auth create --environment https://<miljø>.crm4.dynamics.com/
pac pcf push --publisher-prefix os
```

Alternativt importeres løsningen i `VirtualControl/Solutions/` som managed/unmanaged.

---

## Sikkerhet

- ✅ Proff-API-nøkler ligger i **Key Vault** (`kv-osb-proff`) og refereres fra app settings.
- ✅ Hemmeligheter er fjernet fra git og dekket av `.gitignore`.
- ⚠️ **Function keys er hardkodet** i `VirtualControl/.../config/config.ts` og
  `htmlResource/ProffRating.html` (og finnes i git-historikk). Disse bør **roteres** og
  ideelt hentes fra konfigurasjon/Key Vault i stedet for å committes.
- ⚠️ Historiske hemmeligheter (gammel `local.settings.json`) lå tidligere i git-historikk –
  vurder rotering av berørte nøkler.

---

## Kjente begrensninger og videre arbeid

Se [`docs/improvement-roadmap.html`](docs/improvement-roadmap.html) for full backlog. Kort:

- **Frontend-cutover:** `config.ts` peker fortsatt på den gamle appen. Bytt til
  `osb-proff-v2` og pensjoner `OSB-proff-company-lookup` når premium-/nøkkelflyt er verifisert.
- **Telemetri** er nå koblet på `osb-proff-v2`, men `APPLICATIONINSIGHTS_CONNECTION_STRING`
  ble kopiert fra den gamle appen – vurder egen App Insights-komponent for v2.
- **In-memory cache** er per-instans; distribuert cache (Redis / Table) er Phase 2.
- **`ProffPremiumCache` mangler TTL** – kredittvurderinger kan bli utdaterte.
- **Negative treff** caches likt som treff (0-resultat fra Proff).
- **Function key-rotering** (se Sikkerhet).

---

## Eldre prosjekter (legacy)

- **`ProffCompanyLookupService_OLD_DotNet6Version/`** – tidligere .NET 6-versjon av
  backend. Erstattet av `LIVE-ProffCompanyLookup` (.NET 10). Beholdt for referanse.
- **`ProffCompanyLookupService/`** – tomt prosjektskall, utdatert.

Begge kan fjernes når .NET 10-versjonen er bekreftet i full drift.

---

## Dokumentasjon og endringslogg

- [`docs/codebase-overview.html`](docs/codebase-overview.html) – teknisk arkitektur,
  risikoregister og .NET 10-migreringsplan.
- [`docs/improvement-roadmap.html`](docs/improvement-roadmap.html) – 24 backlog-punkter
  (caching, observability, arkitektur, frontend, drift, data, kvalitet).
- [`docs/billing-pipeline-plan.html`](docs/billing-pipeline-plan.html) – design for
  forbruk/fakturering (event-logg + prising-som-data).
- [`CHANGES.md`](CHANGES.md) – endringslogg med ID-er (f.eks. `BE-1`, `FE-2`, `Q-1`),
  hvor hver post beskriver hva/hvorfor/hvilke filer/forventet effekt/verifisering.
- [`docs/archive/README.original.md`](docs/archive/README.original.md) – opprinnelig,
  arkivert README.
