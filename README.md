# CloudRavel — Multi-Cloud AIOps Operating Platform

## Executive Summary

CloudRavel is a multi-tenant **AIOps operating platform** for enterprise cloud
estates spanning **Azure, AWS, and GCP**. It provides inventory baselines, change
intelligence, security posture tracking, **proactive anomaly detection, incident
management, and gated auto-remediation** from a single control plane, with an AI
operations assistant (any OpenAI-compatible model — configurable endpoint and API key).

> **Status:** Actively hardened for multi-tenant use. The local Docker/OrbStack stack
> and Helm chart are the supported run paths. Treat default credentials (`admin` /
> `ChangeMe123!`, OpenBao `root`) as **local-dev only**. See **Known limitations**
> below before production deployment.

### Known limitations

| Area | Reality today |
|---|---|
| Default credentials | Seeded `admin` / `ChangeMe123!` and OpenBao token `root` are for local/dev only |
| Org SSO UI | Settings are stored; **login federation is not enforced** (`enforcementStatus: not_implemented`) |
| Live cloud collection | Requires `Platform:Environment=Production` and real cloud credentials |
| Lighthouse ARM generation | Manual / customer-side process — not fully automated in-app |
| APIM / private endpoints | Optional Azure networking (Terraform modules), not the default local path |
| Login rate limit | In-process only; multi-instance needs a shared store (e.g. Redis) |
| JWT lifetime | Local tokens default to **4 hours** (configurable via `LocalAuth:TokenLifetimeHours`) |

### What "MSP replacement" means here

| MSP function | Platform capability |
|---|---|
| 24/7 monitoring | Baseline-driven anomaly detectors run every 15 minutes across all tenants |
| Ticketing / incident queue | Auto-created incidents with severity-based SLA clocks and full timelines |
| Runbook execution | Allow-listed remediation playbooks (Azure/AWS/GCP) behind a human approval gate |
| Change review | Change classification, drift detection, first-seen-actor alerting |
| Cost management | Advisor-driven waste detection, right-sizing proposals, cost anomaly alerts |
| Security operations | Defender/Policy regression detection with auto-proposed reversions |
| Monthly reporting | Live operations dashboard: MTTR, SLA breaches, remediation history |

Remediation is **gated by default**: the platform (and its AI) can only *propose* actions
from the playbook catalog; a human approves or rejects each one. Tenants can opt into
auto-approval for low-risk playbooks — high-risk actions always require a human.

### Core Design Principles

1. **Source-of-Truth Hierarchy** — Azure Resource Inventory (ARI) snapshots are the baseline;
   Resource Graph Change History provides delta streams; Activity Log provides audit trails;
   Advisor/Policy/Defender provide recommendations. The AI layer never invents facts.

2. **ARI is Required** — Resource Graph Change History is limited to ~14 days and records only
   deltas. ARI delivers full-state snapshots for baseline and long-term history. Removing ARI
   would make it impossible to reconstruct a tenant's state.

3. **Multi-Tenant Zero Trust** — One control-plane tenant (MSP), many customer tenants. Data
   is logically and cryptographically isolated. No cross-tenant data leakage.

4. **Propose, Never Surprise** — Every automated action maps to an allow-listed playbook and
   passes through the approval gate. There is no free-form execution path, the AI cannot act
   directly, and every transition (proposed → approved → executed) is persisted for audit.

5. **Multi-Cloud, One Model** — AWS accounts and GCP projects normalize into the same
   inventory, anomaly, and remediation model as Azure. Credentials live only in the configured
   secret store (OpenBao — self-hosted, cloud-agnostic).

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        MSP Control Plane Tenant                         │
│                                                                         │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌────────────┐  │
│  │  Next.js UI  │  │  API Gateway │  │  Functions   │  │  AI Agent  │  │
│  │  (Static     │──│  (APIM)      │──│  (Backend)   │──│  Service   │  │
│  │   Web App)   │  │              │  │              │  │            │  │
│  └──────────────┘  └──────────────┘  └──────┬───────┘  └─────┬──────┘  │
│                                             │                │          │
│  ┌──────────────┐  ┌──────────────┐  ┌──────┴───────┐  ┌────┴───────┐  │
│  │  Azure SQL   │  │  Blob        │  │  Service Bus │  │  Azure     │  │
│  │  (Tenant-    │  │  Storage     │  │  (Job Queue) │  │  OpenAI    │  │
│  │   isolated)  │  │  (Snapshots) │  │              │  │            │  │
│  └──────────────┘  └──────────────┘  └──────────────┘  └────────────┘  │
│                                                                         │
│  ┌──────────────┐  ┌──────────────┐                                     │
│  │  OpenBao     │  │  Azure       │                                     │
│  │  (Secrets)   │  │  Automation  │ ← Runs ARI per tenant               │
│  └──────────────┘  │  (Scheduler) │                                     │
│                     └──────────────┘                                     │
└─────────────────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│  Customer   │     │  Customer   │     │  Customer   │
│  Tenant A   │     │  Tenant B   │     │  Tenant N   │
│ (Lighthouse │     │ (App Reg +  │     │ (Lighthouse │
│  delegated) │     │  Reader)    │     │  delegated) │
└─────────────┘     └─────────────┘     └─────────────┘
```

## Component Responsibilities

| Component | Responsibility |
|---|---|
| **Next.js Frontend** | SPA with Entra ID SSO *and* local username/password auth, tenant switcher, inventory explorer, change timeline, dashboards |
| **Azure Functions Backend** | REST API for inventory, changes, recommendations, AI queries, anomalies, incidents, remediation approvals, cloud accounts |
| **AIOps Engine** | Timer-driven anomaly detectors (change velocity, security regression, config drift, unusual actors, sprawl, cost, stale telemetry) + gated remediation executor |
| **Multi-Cloud Adapters** | AWS (SigV4 REST) + GCP (service-account JWT REST) inventory collection and playbook execution; Azure via ARM |
| **Azure SQL** | Tenant-isolated relational store with Row-Level Security (14 tables) — any SQL Server-compatible engine works (local dev uses Azure SQL Edge) |
| **Blob Storage** | Raw ARI snapshot files (normalized JSON) per tenant — Azurite locally |
| **Azure Automation** | Scheduled ARI runs against each Azure customer tenant (Azure-only by nature — ARI itself is an Azure Resource Graph tool) |
| **Job Queue** | Snapshot ingestion, fed by the ARI runbook. Service Bus when configured; otherwise a SQL-table-backed queue — no infra dependency beyond the database itself |
| **AI (OpenAI-compatible)** | Configurable endpoint + API key — the official OpenAI API or any compatible server — 16 tools, function-calling only, persona modes (analyst / operations / security / cost) |
| **OpenBao** | Per-tenant credentials, connection strings — self-hosted, Vault-API-compatible secret store (optional) |
| **VNet + Private Endpoints** | Network isolation for SQL, Storage, Service Bus, OpenAI (Azure deployments) |

---

## Quick Start

### Run locally with Docker / OrbStack (recommended)

The platform runs entirely locally — no Azure subscription, no Entra tenant
required. This is also the reference setup for self-hosting on any cloud:
SQL Server-compatible database + blob-compatible storage are the only two
infrastructure dependencies (Service Bus is optional — see below).

```bash
cp .env.example .env
# Edit .env: set SQL_SA_PASSWORD, LOCAL_AUTH_JWT_SIGNING_KEY, and OPENAI_API_KEY
# at minimum. Leave AZURE_AD_* blank to skip Entra ID entirely.

make up          # build + start detached (safe to close the shell)
# or, plain Compose (stays attached until you detach it yourself):
#   docker compose up -d --build
```

- Frontend: http://localhost:3000
- API: http://localhost:7071/api
- First boot applies schema migrations (`001`–`012`) automatically and creates
  a default local **system admin** only (no demo orgs/clouds):
  **username `admin`, password `ChangeMe123!`** — change this immediately
  outside of local/dev use; the hash is public (it's in source control).
  Optional Contoso demo rows: run `database/seed-demo-data.sql` manually.

Sign in with that account, or with Microsoft if you've set `AZURE_AD_TENANT_ID`
/ `AZURE_AD_CLIENT_ID` in `.env` — both login paths work side by side.

Service Bus is not required to run any of this: the one function that
consumes it (`SnapshotIngestionQueueTimer`, fed by the ARI Automation runbook)
falls back to a SQL-table-backed queue when no Service Bus connection is
configured, so the Functions host never depends on it just to start.

> **Renaming the repo folder?** Docker Compose derives its project name from
> the directory name by default, which prefixes every volume it manages
> (`mssql-data`, `azurite-data`, etc). Renaming the checked-out folder — e.g.
> from `azureinventorymanager` to `cloudravel` — therefore creates a *new*,
> empty set of volumes on next `docker compose up`, orphaning any local data
> you've already collected. To rename the folder without losing data, either
> set `COMPOSE_PROJECT_NAME` to the old project name before renaming (`export
> COMPOSE_PROJECT_NAME=azureinventorymanager` in `.env` or your shell), or
> migrate the volumes manually (`docker volume ls`, then copy their contents
> to the new project's auto-generated volume names).

### Manual setup (without Docker)

#### Prerequisites

- Node.js 20+
- .NET 8 SDK
- SQL Server (local or Azure) for development
- Azure CLI (`az`) + subscription — only needed to deploy to Azure or connect
  real Azure/AWS/GCP tenants; not needed to run the platform itself

#### Backend

```bash
cd src/backend
dotnet restore
dotnet build

# Run the Functions host locally
cd CloudRavel.Api
func start
```

#### Frontend

```bash
cd src/frontend
npm install
npm run dev
# Opens at http://localhost:3000
```

#### Database

```bash
# Apply migrations in order to a local or Azure SQL instance
cd database
sqlcmd -S localhost -d cloudraveldb -i 001-schema.sql
sqlcmd -S localhost -d cloudraveldb -i 002-fix-rls-bypass.sql
sqlcmd -S localhost -d cloudraveldb -i 003-aiops-multicloud.sql
sqlcmd -S localhost -d cloudraveldb -i 004-local-auth.sql
sqlcmd -S localhost -d cloudraveldb -i 005-job-queue.sql
```

### Run the full stack with Docker (local / OrbStack)

The repo ships a self-contained, **cloud-agnostic** stack — SQL Server, OpenBao
(secret store), the Azure Storage emulator, the Functions API, and the Next.js
UI — so `docker compose up` brings everything up with no dependency on any
Azure-only service. Schema migrations run automatically; the only seed is the
bootstrap `admin` user (no demo organization or inventory).

```bash
cp .env.example .env
# Set MSSQL_SA_PASSWORD and LOCAL_AUTH_JWT_SIGNING_KEY.
# For live cloud collection: PLATFORM_ENVIRONMENT=Production

docker compose up --build
```

Then open **http://localhost:3000** and sign in with the bootstrap local admin:

> username **`admin`** · password **`ChangeMe123!`**  *(dev default — change it)*

Create your organization, users, and cloud connections in the UI. Optional
Contoso demo dataset: apply `database/seed-demo-data.sql` manually after migrate.

| Service | URL / Port | Notes |
|---|---|---|
| Web UI | http://localhost:3000 | nginx serves the static export and proxies `/api` → the Functions host (single origin, no CORS) |
| API (direct) | http://localhost:7071/api/health | The browser reaches the API via `/api` through the UI |
| OpenBao | http://localhost:8200 | Dev mode, root token `root` — the cloud-agnostic secret store |
| SQL Server | localhost:1433 | `sa` / `MSSQL_SA_PASSWORD` |

Runs anywhere by design:
- **Secrets** → OpenBao (self-hosted, Vault-API-compatible) instead of Key Vault.
- **Auth** → local username/password. Entra ID SSO is optional — set the
  `NEXT_PUBLIC_AZURE_AD_*` values in `.env` (and rebuild `web`) to enable it.
- **Job queue** → `DatabaseJobQueue` (a SQL table) instead of Service Bus, so the
  Functions host never hard-depends on Service Bus to start.

Notes:
- On Apple Silicon, `mssql`, the migration tools, and the `api` runtime (the
  Azure Functions base image is amd64-only) run under `linux/amd64` emulation,
  handled transparently by OrbStack. The API build stage and the `web` container
  are native arm64.
- The `migrator` service applies `database/001`–`012` once each (ledger
  `dbo.__migrations`). No Contoso demo seed.
- Live cloud timers/collection need `PLATFORM_ENVIRONMENT=Production` and real
  credentials in OpenBao. Compose defaults to Development (safe; no live collect).
  AI Insights needs `OPENAI_*` (or Admin → System Settings).
- To ship an update: `git pull && docker compose up --build` (add `--force-recreate`
  if a container is caching an old image).
- **Clean slate** (wipe DB + re-bootstrap admin only):
  `docker compose down -v && docker compose up -d --build`

#### Local Configuration

Copy `src/backend/CloudRavel.Api/local.settings.json` and set:

| Setting | Description |
|---|---|
| `SqlConnectionString` | SQL Server connection string |
| `Storage__ConnectionString` | Blob Storage connection string (or Azurite for local dev) |
| `ServiceBusConnection` | Optional — Service Bus connection string. Omit to use the built-in SQL-backed job queue instead. |
| `OpenBao__Address` | OpenBao server address, e.g. `http://localhost:8200` (optional — omit to run without credential storage) |
| `OpenBao__Token` | OpenBao auth token |
| `OpenAI__ApiKey` | API key for any OpenAI-compatible endpoint |
| `OpenAI__BaseUrl` | Optional — omit to use the official OpenAI API; point at a self-hosted/compatible server otherwise |
| `OpenAI__Model` | Model name (default: `gpt-4o-mini`) |
| `LocalAuth__JwtSigningKey` | Signs local-login JWTs — required for the local username/password login path |
| `AzureAd__TenantId` | Entra ID tenant for SSO (optional — omit to run local-auth-only) |
| `AzureAd__ClientId` | Entra ID app registration client ID (optional) |

At least one of `LocalAuth__JwtSigningKey` or the `AzureAd__*` pair must be
set for anyone to be able to log in.

---

## Project Structure

```
├── README.md
├── database/
│   └── 001-schema.sql              # 14 tables, RLS, views, stored procedures
├── src/
│   ├── backend/
│   │   ├── CloudRavel.Api/
│   │   │   ├── Functions/
│   │   │   │   ├── InventoryFunctions.cs   # GET /api/inventory/*
│   │   │   │   ├── ChangeFunctions.cs      # GET /api/changes/*
│   │   │   │   ├── RecommendationFunctions.cs  # GET /api/recommendations/*
│   │   │   │   ├── AiFunctions.cs          # POST /api/ai/query
│   │   │   │   ├── TenantFunctions.cs      # GET/POST /api/tenants, GET /api/dashboard
│   │   │   │   ├── HealthFunctions.cs      # GET /api/health, /api/health/ready
│   │   │   │   └── WorkerFunctions.cs      # Service Bus triggers
│   │   │   ├── Middleware/
│   │   │   │   └── TenantContextMiddleware.cs
│   │   │   ├── Program.cs
│   │   │   └── host.json
│   │   ├── CloudRavel.Core/
│   │   │   ├── Interfaces/
│   │   │   │   ├── IRepositories.cs        # ITenantRepository, IInventoryRepository, etc.
│   │   │   │   ├── IServices.cs            # IAzureCredentialFactory, IAriIngestionService, etc.
│   │   │   │   └── IUserRepository.cs      # IUserRepository, IAuditRepository
│   │   │   ├── Models/                     # Tenant, Inventory, ResourceChange, Recommendations, Users
│   │   │   ├── DTOs/ApiDtos.cs             # All API DTOs
│   │   │   ├── Exceptions/AimExceptions.cs # Domain exception hierarchy
│   │   │   └── AI/                         # Tool definitions, system prompts
│   │   └── CloudRavel.Infrastructure/
│   │       ├── Data/
│   │       │   ├── TenantDbConnectionFactory.cs  # RLS session context enforcement
│   │       │   ├── InventoryRepository.cs
│   │       │   ├── TenantRepository.cs
│   │       │   ├── ChangeRepository.cs
│   │       │   ├── RecommendationRepository.cs
│   │       │   ├── UserRepository.cs
│   │       │   └── AuditRepository.cs
│   │       └── Azure/
│   │           ├── AzureCredentialFactory.cs     # Lighthouse vs App Registration credentials
│   │           ├── AriIngestionService.cs        # Blob → SQL ingestion pipeline
│   │           ├── ChangePollingService.cs       # Resource Graph change detection
│   │           └── RecommendationSyncService.cs  # Advisor/Policy/Defender sync
│   └── frontend/                    # Next.js 14 + TypeScript + Tailwind
│       └── src/
│           ├── app/                 # App Router pages
│           │   ├── page.tsx         # Dashboard
│           │   ├── inventory/       # Inventory grid + resource detail
│           │   ├── changes/         # Change timeline
│           │   ├── security/        # Defender findings
│           │   ├── governance/      # Policy compliance
│           │   ├── ai/              # AI chat
│           │   └── tenants/         # Tenant management + onboarding
│           ├── components/          # Shared UI components
│           ├── contexts/            # TenantContext (multi-tenant state)
│           └── lib/
│               ├── api.ts           # API client (MSAL auth + tenant headers)
│               ├── hooks.ts         # SWR data-fetching hooks
│               ├── types.ts         # TypeScript type definitions
│               └── auth.ts          # MSAL configuration
├── infra/
│   └── terraform/
│       ├── main.tf                  # Core Azure resources
│       ├── variables.tf             # Input variables & validation
│       ├── versions.tf              # Provider versions & backend config
│       ├── outputs.tf               # Deployment outputs
│       ├── terraform.tfvars.example # Example variable values
│       └── modules/
│           ├── networking/          # VNet, private endpoints, DNS zones
│           └── monitoring/          # Diagnostic settings, alert rules
├── automation/                      # ARI runbooks
└── .github/workflows/
    ├── build.yml                    # CI: build + test + validate
    └── deploy.yml                   # CD: infra → backend → frontend → database
```

---

## API Reference

### Auth

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/auth/login` | Local username/password login. Returns a JWT valid for the `X-Tenant-Id`-scoped endpoints below, same as an Entra ID token. Anonymous — this is the login endpoint itself. |

### Tenant Management

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/tenants` | List all tenants with summary stats |
| `GET` | `/api/tenants/{id}` | Get tenant details |
| `POST` | `/api/tenants` | Onboard a new tenant |
| `PATCH` | `/api/tenants/{id}/status` | Update tenant status |
| `GET` | `/api/dashboard` | Aggregated dashboard for current tenant |

### Inventory

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/inventory/resources` | Paginated resource list (filter by type, subscription, RG) |
| `GET` | `/api/inventory/resources/{id}` | Resource detail with properties, networking, security config |
| `GET` | `/api/inventory/summary` | Resource type summary/counts |
| `GET` | `/api/inventory/snapshots` | Snapshot history |
| `POST` | `/api/inventory/snapshots/trigger` | Trigger an on-demand snapshot |

### Changes

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/changes` | Paginated change feed (filter by date, resource, classification) |
| `GET` | `/api/changes/recent` | Last 100 changes |
| `GET` | `/api/changes/timeline` | Change timeline buckets (auto-bucketed hourly/daily/weekly) |

### Recommendations

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/recommendations/advisor` | Azure Advisor recommendations |
| `GET` | `/api/recommendations/policy` | Policy compliance records |
| `GET` | `/api/recommendations/defender` | Defender for Cloud findings |
| `PATCH` | `/api/recommendations/{id}/lifecycle` | Dismiss/snooze/reactivate a recommendation |

### AIOps — Anomalies, Incidents, Remediation

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/operations/summary` | Live ops dashboard payload (anomalies, incidents, SLA, approvals, MTTR) |
| `GET` | `/api/anomalies` | Anomaly queue (filter by status/severity/kind) |
| `PATCH` | `/api/anomalies/{id}/status` | Acknowledge / resolve / suppress / mark false positive |
| `GET` | `/api/incidents` | Incident queue with SLA state |
| `GET` | `/api/incidents/{id}` | Incident detail with full event timeline |
| `PATCH` | `/api/incidents/{id}` | Status transitions, notes |
| `GET` | `/api/remediations` | Remediation actions (`?status=PendingApproval` = approval queue) |
| `POST` | `/api/remediations` | Manually propose a playbook action |
| `POST` | `/api/remediations/{id}/approve` | Approve a gated action (executes immediately) |
| `POST` | `/api/remediations/{id}/reject` | Reject a gated action |
| `GET` | `/api/remediations/playbooks` | Allow-listed playbook catalog (Azure/AWS/GCP) |
| `GET` | `/api/cloud-accounts` | Linked AWS accounts / GCP projects |
| `POST` | `/api/cloud-accounts` | Link an AWS account or GCP project (credentials → secret store) |

### AI

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/ai/query` | Natural language query with tool-calling (16 tools). Optional `mode`: `analyst` \| `operations` \| `security` \| `cost`. The `operations` persona can propose gated remediations via `propose_remediation` — it never executes directly. |

### Health

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/health` | Component-level health check |
| `GET` | `/api/health/ready` | Readiness probe for load balancers |

All endpoints (except health and login) require:

- `Authorization: Bearer <JWT>` — an Entra ID token, or a token from `POST /api/auth/login`. Both are accepted on every request; whichever issued the token is detected automatically.
- `X-Tenant-Id: <guid>` — Target tenant ID

---

## Security Model

### Multi-Tenant Data Isolation

- **Row-Level Security (RLS)** — Every SQL query runs with `SESSION_CONTEXT('tenant_id')` set. A security predicate (`fn_tenant_security_predicate`) enforces isolation at the database level.
- **Admin bypass** — `SESSION_CONTEXT('bypass_rls')` = 1 for cross-tenant admin queries (tenant listing, dashboard aggregation).
- **Tenant-scoped middleware** — `TenantContextMiddleware` extracts and validates the `X-Tenant-Id` header on every request.

### Authentication & Authorization

- **Two independent login paths, both enforced identically:**
  - **Entra ID (MSAL)** — Frontend authenticates via MSAL popup/redirect.
  - **Local (username/password)** — `POST /api/auth/login` verifies a PBKDF2-HMACSHA256 hash and issues a platform-signed JWT. Lets the platform run without an Entra tenant at all.
- The backend validates whichever token a request carries — a policy scheme inspects the token's issuer and forwards it to the matching `JwtBearer` scheme (`EntraId` or `Local`) for real signature/expiry validation. `[Authorize]` is enforced on every endpoint except health and login.
- **Two credential models per Azure customer tenant** (for collecting *their* inventory — independent of how *your* users log in above):
  - **Azure Lighthouse** — DefaultAzureCredential with delegated permissions (recommended)
  - **App Registration** — Client secret stored in the secret store (OpenBao), resolved per tenant

### Network Security

- All PaaS services (SQL, Storage, Service Bus, Key Vault, OpenAI) use private endpoints
- VNet integration for Azure Functions (production)
- Network ACLs default to `Deny` with `AzureServices` bypass

---

## Infrastructure Deployment

### Terraform Deployment

```bash
# Login to Azure
az login
az account set --subscription <subscription-id>

# Initialise Terraform
cd infra/terraform
cp terraform.tfvars.example terraform.tfvars
# Edit terraform.tfvars with your values

terraform init
terraform plan -out=tfplan
terraform apply tfplan
```

> **Remote state (recommended for teams):** Uncomment the `backend "azurerm"` block in
> `versions.tf` and configure a storage account for state locking.

### What Gets Deployed

| Resource | SKU (prod) | SKU (dev) |
|---|---|---|
| Log Analytics | PerGB2018 | PerGB2018 |
| Application Insights | — | — |
| Key Vault | Standard | Standard |
| Storage Account | Standard_LRS | Standard_LRS |
| Service Bus | Standard | Standard |
| Azure SQL | S3 | S1 |
| Function App Plan | EP1 (Elastic Premium) | Y1 (Consumption) |
| Azure OpenAI (GPT-5.5, GlobalStandard) | S0 | S0 |
| Automation Account | Basic | Basic |
| Static Web App | Standard | Free |
| VNet + 5 Private Endpoints | ✓ | ✓ |
| Alert Rules | ✓ (5xx, latency, DTU, dead letters) | — |

---

## Deploy to Kubernetes

CloudRavel ships a Helm chart (`deploy/helm/cloudravel`) that runs the whole stack on any
cluster — no Azure required. CI publishes the container images and the chart to GHCR
(`ghcr.io/<owner>/cloudravel-{api,web,migrator}` and `oci://ghcr.io/<owner>/charts`); you
run `helm install` against your own cluster.

```bash
helm install cloudravel oci://ghcr.io/<owner>/charts/cloudravel \
  --set secrets.mssqlSaPassword='Your_Strong_Passw0rd!' \
  --set secrets.localAuthJwtSigningKey="$(openssl rand -hex 32)" \
  --set ingress.host=cloudravel.example.com
```

Out of the box this brings up bundled `mssql` (Azure SQL Edge), `azurite`, and `openbao`
plus the `api` and `web` deployments; a one-time migration Job creates the schema and the
API becomes Ready once it completes. Reach the UI at the ingress host (or
`kubectl port-forward svc/cloudravel-web 8080:80`). Default login: **`admin` /
`ChangeMe123!`** — change it immediately.

**Use external managed services** instead of a bundled dependency by disabling it and
pointing at your own:

```bash
  --set mssql.enabled=false \
  --set externalMssql.host=sql.example.com --set externalMssql.user=cloudraveladmin \
  --set openbao.enabled=false --set externalOpenBao.address=https://vault.example.com:8200 \
  --set azurite.enabled=false --set externalStorage.connectionString='<AzureWebJobsStorage>'
```

Provide your own Kubernetes Secret (instead of the chart creating one) with
`--set secrets.existingSecret=my-secret` (keys: `mssql-sa-password`,
`local-auth-jwt-signing-key`, `openbao-token`, `openai-api-key`, `azure-webjobs-storage`).
See `deploy/helm/cloudravel/values.yaml` for the full surface (image tags, ingress TLS,
replica counts, resources, OpenAI/Entra settings).

> **Notes.** The `api` image uses the amd64-only Azure Functions base image, so schedule it
> on amd64 nodes. The bundled OpenBao runs in dev mode (in-memory) — its secrets do not
> survive a restart; use an external Vault/OpenBao for production. Enabling Entra SSO
> requires rebuilding the `web` image with the `NEXT_PUBLIC_*` build args (they are baked at
> build time); local username/password login works with the published image as-is.

---

## Database Schema

22 tables across 8 domains:

| Domain | Tables |
|---|---|
| **Tenants** | `tenants`, `tenant_subscriptions` |
| **Users** | `users` (Entra *or* local — `auth_provider`/`username`/`password_hash`), `user_tenant_access` |
| **Inventory** | `inventory_snapshots`, `inventory_resources` (multi-cloud `provider` column), `latest_snapshots` |
| **Changes** | `resource_changes`, `activity_log_events` |
| **Recommendations** | `advisor_recommendations`, `policy_compliance`, `defender_findings` |
| **AIOps** | `anomalies`, `metric_baselines`, `incidents`, `incident_events`, `remediation_playbooks`, `remediation_actions` |
| **Multi-Cloud** | `cloud_accounts` |
| **Platform** | `ai_query_log`, `audit_events`, `job_queue` (portable queue — see below) |

Key features:
- RLS security policy on all tenant-scoped tables
- Cleanup stored procedure (`usp_cleanup_old_data`) for retention management
- Summary views (`vw_tenant_resource_summary`, `vw_tenant_security_posture`, `vw_tenant_change_velocity`)

---

## CI/CD Workflows

### Build (`build.yml`)

- Triggers on `workflow_dispatch` and on push to `main`/`orbstack-integration` and `v*` tags
- Backend: `dotnet restore` → `dotnet build` → `dotnet publish`
- Frontend: `npm ci` → `npm run lint` → `npm run build`
- Terraform: `terraform fmt -check` → `terraform init -backend=false` → `terraform validate`
- **Images**: builds & pushes `cloudravel-{api,web,migrator}` to GHCR (amd64), tagged with
  the commit SHA, branch, and — on a `v*` tag — the semver + `latest`
- **Chart**: `helm lint` → `helm package` → `helm push` the chart to `oci://ghcr.io/<owner>/charts`

### Deploy to Azure (`deploy.yml`)

- Triggers on `workflow_dispatch`
- 4 stages: Infrastructure (Terraform) → Backend (Functions) → Frontend (Static Web App) → Database
- Uses OIDC federation (no stored secrets)

> The **Kubernetes** path is deliberately "publish only" — CI builds/pushes images and the
> Helm chart; you deploy them to your own cluster with `helm install` (see *Deploy to
> Kubernetes* above). No cluster credentials are stored in CI.

---

## Technology Stack

| Layer | Technology |
|---|---|
| Backend | .NET 8, Azure Functions v4 (isolated worker) |
| ORM | Dapper 2.1 |
| Frontend | Next.js 14, React 18, TypeScript 5, Tailwind CSS 3 |
| Auth | MSAL.js (Entra ID SSO) + PBKDF2/JWT (local username/password) |
| Data Fetching | SWR |
| Charts | Recharts |
| IaC / Deploy | Terraform (azurerm ~> 4.0, Azure), Helm chart (any Kubernetes), Docker Compose (local/self-hosted) |
| CI/CD | GitHub Actions |
| AI | Any OpenAI-compatible endpoint (configurable base URL + API key) with function calling |

---

## License

Proprietary — MSP Internal Use
