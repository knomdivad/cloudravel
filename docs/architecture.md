# Architecture Overview

## Data & Control Flows

### Flow 1: Inventory Snapshot (Scheduled — every 6 hours default)

```
Azure Automation (Timer)
  → Runs ARI PowerShell runbook per tenant
  → ARI connects to customer tenant via Lighthouse / App Reg
  → ARI produces Excel + JSON inventory
  → Uploads raw output to Blob Storage (container per tenant)
  → Sends ServiceBus message: { type: "snapshot-ready", tenantId, blobPath }
  → Worker Function picks up message
  → Parses ARI output, normalizes into relational schema
  → Inserts into Azure SQL (tenant-scoped, snapshot_id-tagged)
  → Updates latest_snapshot pointer for tenant
```

### Flow 2: Change Polling (Scheduled — every 15 minutes)

```
Timer-triggered Function
  → For each active tenant:
    → Query Resource Graph: resourcechanges (last 15 min window)
    → Query Resource Graph: resourcecontainerchanges (last 15 min window)
    → For each change:
      → Join to inventory (by resource ID)
      → Classify change (security / governance / cost / operational)
      → Persist to changes table
      → If security-impacting: emit alert via Service Bus
```

### Flow 3: Recommendation Sync (Scheduled — every 1 hour)

```
Timer-triggered Function
  → For each active tenant:
    → Query Azure Advisor REST API
    → Query Azure Policy compliance REST API
    → Query Defender for Cloud REST API
    → Normalize into unified recommendation schema
    → Upsert into recommendations table
    → Track lifecycle transitions (new → acknowledged → resolved)
```

### Flow 4: AI Query (User-initiated)

```
User → Frontend → API (POST /api/ai/query)
  → Validate tenant scope (RBAC)
  → Construct system prompt with tenant context
  → Send to the configured OpenAI-compatible endpoint with tool definitions
  → OpenAI calls tools (get_inventory_snapshot, get_resource_changes, etc.)
  → API executes tools against Azure SQL (tenant-scoped)
  → Returns tool results to OpenAI
  → OpenAI synthesizes response with citations
  → Returns to user
```

### Flow 5: Proactive Anomaly Detection (Scheduled — every 15 minutes)

```
Timer-triggered Function (offset :05/:20/:35/:50, after change polling)
  → For each active tenant with AIOps monitoring enabled:
    → Run detectors against authoritative stores (never live cloud APIs):
      1. ChangeVelocitySpike      — 24h change count vs 30-day daily baseline (z ≥ 3)
      2. SecurityPostureRegression— new Critical/High Defender findings in 24h
      3. ConfigurationDrift       — critical/high security changes per resource
      4. UnusualActorActivity     — actors absent from the 30-day baseline
      5. ResourceSprawl           — resource count vs EWMA baseline (≥20% growth)
      6. CostAnomaly              — Advisor savings total jump (≥$1k and ≥50%)
      7. StaleTelemetry           — snapshot older than 2× configured cadence
    → Dedup by fingerprint (open anomalies refresh; new ones insert)
    → High/Critical anomalies open (or join) an incident with an SLA clock
    → Unambiguous security drifts auto-PROPOSE playbook remediations (gated)
```

### Flow 6: Gated Remediation

```
Proposal (anomaly engine | AI propose_remediation tool | operator via API)
  → Playbook lookup (allow-list; unknown actions are rejected)
  → Approval gate resolution:
      tenant mode = disabled → Proposed  (visible, inert until human approves)
      tenant mode = gated    → PendingApproval (approvals queue)
      tenant mode = auto     → low-risk playbooks auto-approve;
                               medium/high and always-gated playbooks stay pending
  → Human approves (POST /remediations/{id}/approve) → executes immediately
      or timer worker drains Approved queue every 5 minutes
  → Provider adapter executes the typed action (ARM REST / AWS SigV4 / GCP REST)
  → Result + errors persisted; incident timeline updated; pending items expire after 7 days
```

### Flow 7: Multi-Cloud Inventory Sync (Scheduled — daily 3 AM, after Azure snapshot)

```
Timer-triggered Function
  → For each connected AWS/GCP account (credentials from the secret store):
    → AWS: Resource Groups Tagging API GetResources per region (SigV4-signed)
    → GCP: Cloud Asset API asset listing (service-account JWT → OAuth token)
    → Normalize into shared inventory model (provider column: aws | gcp)
    → Replace that provider's rows in the tenant's LATEST snapshot
  → Inventory explorer, dashboards, and AI tools see one merged multi-cloud view
```

### Flow 8: User Authentication & Tenant Selection

```
User → Next.js Frontend
  → Either: MSAL redirect to Entra ID (SSO)
     Or:    POST /api/auth/login with username/password (local auth)
  → Obtains an access token either way (Entra JWT, or a platform-signed JWT)
  → Frontend sends token to API
  → API's policy scheme detects which one issued it and validates against the
    matching JwtBearer scheme (EntraId or Local) — both are accepted uniformly
  → Resolves user → tenant permissions from RBAC table
  → Returns authorized tenant list
  → User selects tenant (or sees default)
  → All subsequent API calls include X-Tenant-Id header
  → API enforces tenant access on every request
```

## Source-of-Truth Data Model

```
                    ┌─────────────────┐
                    │   ARI Snapshot   │ ← Full state baseline
                    │   (every 6h)    │
                    └────────┬────────┘
                             │
                    ┌────────┴────────┐
                    │  Inventory      │ ← Normalized resources
                    │  Resources      │    per snapshot
                    └────────┬────────┘
                             │
              ┌──────────────┼──────────────┐
              │              │              │
     ┌────────┴───────┐  ┌──┴──────────┐  ┌┴───────────────┐
     │ Change History │  │ Advisor     │  │ Policy         │
     │ (RG + Activity)│  │ Recs        │  │ Compliance     │
     └────────────────┘  └─────────────┘  └────────────────┘
              │              │              │
              └──────────────┼──────────────┘
                             │
                    ┌────────┴────────┐
                    │   AI Agent      │ ← Reads freely; its ONLY write path
                    │  (OpenAI-compat,│    is propose_remediation, which lands
                    │    Tool Use)    │    in the gated approval queue
                    └─────────────────┘
```

## Tenant Isolation Model

### Azure SQL Row-Level Security

Every table includes a `tenant_id` column. A security policy function filters rows
based on the `SESSION_CONTEXT('tenant_id')` value set by the API at connection time.

This means:
- Even if code has a bug that omits a WHERE clause, RLS prevents cross-tenant reads
- The API sets `sp_set_session_context @key = 'tenant_id', @value = <tid>` on every connection
- No tenant can see another tenant's data, ever

### Blob Storage Isolation

Each tenant gets a dedicated container: `snapshots-{tenant_id}`.
SAS tokens (when needed) are scoped to a single container with read-only access.

### AI Query Isolation

The system prompt includes the tenant context. Tool calls are executed with the
tenant session context active. The AI model cannot request data from other tenants
because the tools themselves enforce the RLS boundary.

## Scaling Strategy

| Component | Scaling Model |
|---|---|
| Azure Functions (API) | Consumption or Premium plan, auto-scale on HTTP triggers |
| Azure Functions (Workers) | Premium plan; snapshot ingestion polls the active job queue (Service Bus or the SQL-backed default) every minute |
| Azure SQL | DTU/vCore scaling; read replicas for dashboards |
| Blob Storage | Unlimited, lifecycle tiering (hot → cool → archive) |
| Azure Automation | Parallel runbook jobs (max 10 concurrent per account) |
| APIM | Standard tier, multi-region for global MSPs |

## Failure Modes & Mitigations

| Failure | Impact | Mitigation |
|---|---|---|
| ARI snapshot fails for a tenant | Stale baseline (6h gap) | Retry with exponential backoff; alert MSP; last-good snapshot remains valid |
| Resource Graph throttled | Missing changes in window | Idempotent polling with overlap windows (query last 20min, dedup) |
| Azure SQL down | Full outage | Geo-replication; failover group; API returns 503 with Retry-After |
| Lighthouse delegation revoked | Tenant becomes inaccessible | Health check detects; marks tenant "degraded"; notifies MSP admin |
| OpenAI quota exhausted | AI features unavailable | Graceful degradation; UI shows raw data; queue AI requests for retry |

## Cost Controls

| Control | Implementation |
|---|---|
| ARI snapshot frequency | Configurable per tenant (default 6h, minimum 1h) |
| Change polling frequency | 15-min default; can be relaxed to 30/60 min |
| Blob lifecycle | Move snapshots >30d to cool, >90d to archive |
| SQL data retention | Partition by month; drop partitions >13 months (configurable) |
| APIM rate limits | Per-tenant rate limits prevent noisy-neighbor |
| Function timeouts | 10-min max per ARI processing job |
