# Azure Inventory Monitor — Multi-Tenant CSP/MSP Platform

## Executive Summary

Azure Inventory Monitor (AIM) is a production-ready, multi-tenant monitoring and recommendation
platform designed for Cloud Solution Providers (CSP), Managed Service Providers (MSP), and
Managed Security Service Providers (MSSP). It provides authoritative inventory baselines,
change intelligence, security posture tracking, and AI-assisted prioritization across
multiple Azure customer tenants — all from a single control plane.

### Core Design Principles

1. **Source-of-Truth Hierarchy** — Azure Resource Inventory (ARI) snapshots are the baseline;
   Resource Graph Change History provides delta streams; Activity Log provides audit trails;
   Advisor/Policy/Defender provide recommendations. The AI layer never invents facts.

2. **ARI is Required** — Resource Graph Change History is limited to ~14 days and records only
   deltas. ARI delivers full-state snapshots for baseline and long-term history. Removing ARI
   would make it impossible to reconstruct a tenant's state.

3. **Multi-Tenant Zero Trust** — One control-plane tenant (MSP), many customer tenants. Data
   is logically and cryptographically isolated. No cross-tenant data leakage.

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
│  │  Key Vault   │  │  Azure       │                                     │
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
| **Next.js Frontend** | SPA with Entra ID auth, tenant switcher, inventory explorer, change timeline, dashboards |
| **Azure Functions Backend** | REST API for inventory, changes, recommendations, AI queries, tenant management |
| **Azure SQL** | Tenant-isolated relational store with Row-Level Security (14 tables) |
| **Blob Storage** | Raw ARI snapshot files (normalized JSON) per tenant |
| **Azure Automation** | Scheduled ARI runs against each customer tenant |
| **Service Bus** | Async job processing (snapshot ingestion, change polling, recommendation sync) |
| **Azure OpenAI** | AI summarization and prioritization (9 tools, function-calling only) |
| **Key Vault** | Per-tenant credentials, connection strings |
| **VNet + Private Endpoints** | Network isolation for SQL, Storage, Service Bus, Key Vault, OpenAI |

---

## Quick Start

### Prerequisites

- Node.js 20+
- .NET 8 SDK
- Azure CLI (`az`)
- PowerShell 7+
- SQL Server (local or Azure) for development
- Azure subscription for deployment

### Backend

```bash
cd src/backend
dotnet restore
dotnet build

# Run the Functions host locally
cd AzureInventoryMonitor.Api
func start
```

### Frontend

```bash
cd src/frontend
npm install
npm run dev
# Opens at http://localhost:3000
```

### Database

```bash
# Apply the schema to a local or Azure SQL instance
cd database
sqlcmd -S localhost -d aimdb -i 001-schema.sql
```

### Local Configuration

Copy `src/backend/AzureInventoryMonitor.Api/local.settings.json` and set:

| Setting | Description |
|---|---|
| `SqlConnectionString` | SQL Server connection string |
| `ServiceBusConnection__fullyQualifiedNamespace` | Service Bus FQDN |
| `Storage__ConnectionString` | Blob Storage connection string |
| `KeyVaultUrl` | Key Vault URI |
| `AzureOpenAiEndpoint` | OpenAI endpoint URL |
| `AzureOpenAiDeployment` | Model deployment name (e.g. `gpt-4o`) |
| `AzureAd__TenantId` | Entra ID tenant for auth |
| `AzureAd__ClientId` | Entra ID app registration client ID |

---

## Project Structure

```
├── README.md
├── database/
│   └── 001-schema.sql              # 14 tables, RLS, views, stored procedures
├── src/
│   ├── backend/
│   │   ├── AzureInventoryMonitor.Api/
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
│   │   ├── AzureInventoryMonitor.Core/
│   │   │   ├── Interfaces/
│   │   │   │   ├── IRepositories.cs        # ITenantRepository, IInventoryRepository, etc.
│   │   │   │   ├── IServices.cs            # IAzureCredentialFactory, IAriIngestionService, etc.
│   │   │   │   └── IUserRepository.cs      # IUserRepository, IAuditRepository
│   │   │   ├── Models/                     # Tenant, Inventory, ResourceChange, Recommendations, Users
│   │   │   ├── DTOs/ApiDtos.cs             # All API DTOs
│   │   │   ├── Exceptions/AimExceptions.cs # Domain exception hierarchy
│   │   │   └── AI/                         # Tool definitions, system prompts
│   │   └── AzureInventoryMonitor.Infrastructure/
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

### AI

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/ai/query` | Natural language query with tool-calling (9 tools) |

### Health

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/health` | Component-level health check |
| `GET` | `/api/health/ready` | Readiness probe for load balancers |

All endpoints (except health) require:

- `Authorization: Bearer <JWT>` — Entra ID token
- `X-Tenant-Id: <guid>` — Target tenant ID

---

## Security Model

### Multi-Tenant Data Isolation

- **Row-Level Security (RLS)** — Every SQL query runs with `SESSION_CONTEXT('tenant_id')` set. A security predicate (`fn_tenant_security_predicate`) enforces isolation at the database level.
- **Admin bypass** — `SESSION_CONTEXT('bypass_rls')` = 1 for cross-tenant admin queries (tenant listing, dashboard aggregation).
- **Tenant-scoped middleware** — `TenantContextMiddleware` extracts and validates the `X-Tenant-Id` header on every request.

### Authentication & Authorization

- **Entra ID (MSAL)** — Frontend authenticates via MSAL popup/redirect. Backend validates JWT tokens.
- **Two credential models per customer tenant:**
  - **Azure Lighthouse** — DefaultAzureCredential with delegated permissions (recommended)
  - **App Registration** — Client secret stored in Key Vault, resolved per tenant

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
| Azure OpenAI | S0 | S0 |
| Automation Account | Basic | Basic |
| Static Web App | Standard | Free |
| VNet + 5 Private Endpoints | ✓ | ✓ |
| Alert Rules | ✓ (5xx, latency, DTU, dead letters) | — |

---

## Database Schema

14 tables across 5 domains:

| Domain | Tables |
|---|---|
| **Tenants** | `tenants`, `tenant_subscriptions` |
| **Users** | `users`, `user_tenant_access` |
| **Inventory** | `inventory_snapshots`, `inventory_resources`, `latest_snapshots` |
| **Changes** | `resource_changes`, `activity_log_events` |
| **Recommendations** | `advisor_recommendations`, `policy_compliance`, `defender_findings` |
| **Platform** | `ai_query_log`, `audit_events` |

Key features:
- RLS security policy on all tenant-scoped tables
- Cleanup stored procedure (`usp_cleanup_old_data`) for retention management
- Summary views (`vw_tenant_resource_summary`, `vw_tenant_security_posture`, `vw_tenant_change_velocity`)

---

## CI/CD Workflows

### Build (`build.yml`)

- Triggers on push/PR to `main`
- Backend: `dotnet restore` → `dotnet build` → `dotnet test` → `dotnet publish`
- Frontend: `npm ci` → `npm run lint` → `npm run build`
- Terraform: `terraform fmt -check` → `terraform init -backend=false` → `terraform validate`

### Deploy (`deploy.yml`)

- Triggers on push to `main` (after build passes)
- 4 stages: Infrastructure → Backend → Frontend → Database
- Uses OIDC federation (no stored secrets)

---

## Technology Stack

| Layer | Technology |
|---|---|
| Backend | .NET 8, Azure Functions v4 (isolated worker) |
| ORM | Dapper 2.1 |
| Frontend | Next.js 14, React 18, TypeScript 5, Tailwind CSS 3 |
| Auth | MSAL.js, Microsoft.Identity.Web |
| Data Fetching | SWR |
| Charts | Recharts |
| IaC | Terraform (azurerm ~> 4.0) |
| CI/CD | GitHub Actions |
| AI | Azure OpenAI (GPT-4o) with function calling |

---

## License

Proprietary — MSP Internal Use
