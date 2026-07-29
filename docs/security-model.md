# Security & Multi-Tenant Model

## Tenant Onboarding Flow

### Option A: Azure Lighthouse (Preferred for CSP)

```
MSP Admin initiates onboarding in the CloudRavel UI
  → CloudRavel generates ARM template with Lighthouse delegation:
      - managedByTenantId: MSP tenant ID
      - authorizations:
          - MSP Reader group → Reader role on customer subscriptions
          - MSP Automation SPN → Reader role (for ARI)
          - MSP Automation SPN → Log Analytics Reader (for Activity Log)
  → Customer admin deploys template in their tenant
  → CloudRavel callback validates delegation
  → CloudRavel creates tenant record:
      - tenant_id, display_name, onboarding_method = 'lighthouse'
      - delegation_template_id
      - active = true
  → First ARI snapshot is triggered immediately
  → Tenant appears in MSP console within minutes
```

### Option B: Per-Tenant App Registration (For non-Lighthouse scenarios)

```
MSP Admin initiates onboarding in the CloudRavel UI
  → CloudRavel provides instructions for customer:
      1. Register the CloudRavel app in customer Entra ID
      2. Grant API permissions: 
         - Microsoft.Graph: Directory.Read.All (delegated)
      3. Assign Azure RBAC:
         - Reader on target subscriptions
         - Security Reader on subscriptions (for Defender)
      4. Create client secret or certificate
      5. Provide: tenant_id, client_id, client_secret (or cert thumbprint)
  → MSP enters credentials
  → CloudRavel stores the secret in the secret store (tagged by tenant_id)
  → CloudRavel validates connectivity:
      - GET /subscriptions?api-version=2022-12-01
  → Creates tenant record with onboarding_method = 'app_registration'
  → First ARI snapshot triggered
```

## Authentication Architecture

### Frontend → API

```
1. User authenticates via MSAL.js to MSP Entra ID (or via local username/password login)
2. Token audience: api://{CLOUDRAVEL_API_CLIENT_ID} (Entra) or cloudravel-api (local)
3. Token includes:
   - oid/sub (user object ID)
   - tid (MSP tenant ID — always the MSP tenant, Entra only)
   - system role resolved server-side from users.global_role (system_admin, member)
   - groups (optional, for team-based access)
4. API validates token:
   - Issuer: https://login.microsoftonline.com/{MSP_TENANT_ID}/v2.0 (Entra) or
     cloudravel-local-auth (local)
   - Audience: api://{CLOUDRAVEL_API_CLIENT_ID} (Entra) or cloudravel-api (local)
   - Signature: via JWKS endpoint (Entra) or the derived HMAC key (local)
5. API resolves user permissions:
   - Query user_tenant_access for the caller's per-organization role
   - system_admin: implicit org_admin on every organization, plus system settings
     and user management
   - org_admin: manage the organization's users, clouds, and SSO settings
   - cloud_admin: manage the organization's clouds; read everything else
   - read_only: read-only access to the organization
```

### API → Customer Tenant

```
Option A (Lighthouse):
  - Use Managed Identity of the Functions app
  - Azure automatically grants cross-tenant Reader via Lighthouse
  - No secrets stored

Option B (App Registration):
  - Retrieve credentials from Key Vault
  - Acquire token for https://management.azure.com/ in customer tenant
  - Use token for ARM/Graph calls
  - Client secret rotation handled by Key Vault rotation policy
```

## RBAC Model

Two independent tiers: a system tier (`users.global_role`) and a per-organization tier
(`user_tenant_access.role`). A `system_admin` acts as `org_admin` on every organization.

| Tier | Role | Scope | Permissions |
|---|---|---|---|
| System | `system_admin` | Global | Create organizations; configure system settings (AI model/endpoint/key); manage all users; implicit `org_admin` everywhere |
| System | `member` | Global | No system privileges; all access comes from per-organization grants below |
| Org | `org_admin` | Per-organization | Manage the organization's users, clouds, and SSO settings; everything `cloud_admin` can do |
| Org | `cloud_admin` | Per-organization | Connect/manage clouds; trigger snapshots; triage anomalies/incidents; propose/approve/reject remediations |
| Org | `read_only` | Per-organization | Read-only view of inventory, changes, recommendations, AI queries; no mutations |

## Data Isolation Enforcement Points

### Layer 1: API (Functions host)
- Custom middleware validates Entra or local JWT on every HTTP trigger
  (`AuthEnforcementMiddleware` + `TenantContextMiddleware`)
- `X-Tenant-Id` must match path org/tenant ids on resource routes (`RequirePathTenantMatch`)
- Inactive users are rejected on every request; Entra callers are JIT-provisioned as `member`
- CORS is an explicit allow-list (`Cors:AllowedOrigins`) — not `*`

Optional Azure front door (APIM / App Gateway) can sit in front of the Function App for
network policy; it is **not** required by the application runtime.

### Layer 2: Application Code
- Tenant-scoped SQL uses `SESSION_CONTEXT('tenant_id')` via `TenantDbConnectionFactory`
- Org/system role gates on mutating endpoints (`RequireOrgRoleAsync` / `RequireSystemAdminAsync`)
- AWS/GCP/Azure app-reg credentials resolve from the configured secret store only
- Remediation execution resolves the playbook + tenant policy on every run
- Audit actor identity is taken from JWT claims only (never client headers)

### Layer 3: Database (RLS)
- Row-Level Security policy on tenant-scoped tables, including AIOps tables
  (`cloud_accounts`, `anomalies`, `metric_baselines`, `incidents`, `incident_events`,
  `remediation_actions`, `inventory_snapshots` FILTER+BLOCK, `audit_events`)
- `remediation_playbooks` is a global allow-list catalog (no tenant data)
- `job_queue` is worker/admin-only (no `tenant_id` column)
- Admin operations use `bypass_rls` session context

### Layer 4: Encryption
- Azure SQL TDE (or equivalent at-rest encryption for the SQL engine in use)
- TLS 1.2+ in transit
- Column-level encryption / per-tenant CMK: **not implemented** (future)

## Secrets Management

The app uses `ISecretStore` with a configurable provider:

| Deployment | Provider | Config |
|---|---|---|
| Docker Compose / Helm (self-host) | OpenBao (Vault API) | `OpenBao:Address`, `OpenBao:Token`, `SecretStore:Provider=OpenBao` |
| Azure (Terraform) | Azure Key Vault | `KeyVault:VaultUri`, `SecretStore:Provider=KeyVault` |

| Secret Type | Storage | Notes |
|---|---|---|
| App Registration client secrets | Secret store | Fail closed if store missing |
| AWS access keys | Secret store (`cloudaccount-{id}`) | Required at link time |
| GCP service account keys | Secret store | Required at link time |
| OpenAI-compatible API key | Secret store or env `OpenAI:ApiKey` | System admin UI writes to store |
| Local auth JWT signing key | Env / K8s secret / Key Vault | **Required at API startup** |
| SQL / Service Bus / Blob (Azure) | Managed Identity preferred | Terraform path |

Credential **values** never appear in API responses, logs, or the AI tool context —
only secret *names* are stored in SQL.

## Audit Trail

All API calls are logged to Application Insights with:
- User identity (oid)
- Tenant context
- Operation type
- Resource IDs accessed
- Timestamp
- Client IP

Audit logs are retained for 2 years (configurable).
Security-impacting operations (onboarding, RBAC changes, data export) generate
additional structured audit events in Azure SQL.

## Remediation Safety Model

The AIOps engine can change customer environments, so its authority is deliberately
narrow and layered (most restrictive layer wins):

```
1. Allow-list      — every action maps to a row in remediation_playbooks with a
                     typed executor per provider. Unknown action types are rejected
                     by the adapters. There is NO free-form execution path.
2. Tenant policy   — auto_remediation_mode: disabled | gated (default) | auto.
                     'auto' only auto-approves Low-risk playbooks.
3. Playbook flags  — always_requires_approval playbooks (e.g. NSG rule removal)
                     can never auto-approve regardless of tenant policy.
4. Approval gate   — gated actions sit in PendingApproval until a human approves
                     or rejects; pending actions expire after 7 days so stale
                     proposals never fire against a drifted environment.
5. State machine   — execution only ever starts from Approved, with an atomic
                     Approved → Executing transition (safe under concurrency).
6. Audit           — every transition, executor result, and error is persisted on
                     the action row and mirrored into the incident timeline.
```

### AI write-path containment

The AI assistant (GPT-5.5 via Azure OpenAI) has exactly one tool with side effects:
`propose_remediation`. It creates a remediation action that enters the same approval
gate as engine- and human-proposed actions — the model cannot approve, execute, or
bypass anything. All other tools are read-only queries against tenant-scoped stores.

### Cloud write permissions

Remediation needs narrowly-scoped write roles beyond the read-only inventory model:

| Provider | Read (inventory) | Write (remediation executors only) |
|---|---|---|
| Azure | Reader via Lighthouse/App Reg | Scoped roles for allow-listed operations, e.g. Storage Account Contributor, VM Contributor, Network Contributor, Tag Contributor on managed scopes |
| AWS | `tag:GetResources`, `sts:GetCallerIdentity` | `ec2:StopInstances`, `s3:PutBucketPublicAccessBlock` |
| GCP | `cloudasset.assets.list` | `compute.instances.stop`, `storage.buckets.update` |

Grant write permissions only for the playbooks a tenant actually enables; the
platform degrades gracefully (execution fails with a recorded error) when a
permission is absent.
