# Security & Multi-Tenant Model

## Tenant Onboarding Flow

### Option A: Azure Lighthouse (Preferred for CSP)

```
MSP Admin initiates onboarding in AIM UI
  → AIM generates ARM template with Lighthouse delegation:
      - managedByTenantId: MSP tenant ID
      - authorizations:
          - MSP Reader group → Reader role on customer subscriptions
          - MSP Automation SPN → Reader role (for ARI)
          - MSP Automation SPN → Log Analytics Reader (for Activity Log)
  → Customer admin deploys template in their tenant
  → AIM callback validates delegation
  → AIM creates tenant record:
      - tenant_id, display_name, onboarding_method = 'lighthouse'
      - delegation_template_id
      - active = true
  → First ARI snapshot is triggered immediately
  → Tenant appears in MSP console within minutes
```

### Option B: Per-Tenant App Registration (For non-Lighthouse scenarios)

```
MSP Admin initiates onboarding in AIM UI
  → AIM provides instructions for customer:
      1. Register AIM app in customer Entra ID
      2. Grant API permissions: 
         - Microsoft.Graph: Directory.Read.All (delegated)
      3. Assign Azure RBAC:
         - Reader on target subscriptions
         - Security Reader on subscriptions (for Defender)
      4. Create client secret or certificate
      5. Provide: tenant_id, client_id, client_secret (or cert thumbprint)
  → MSP enters credentials
  → AIM stores secret in Key Vault (tagged by tenant_id)
  → AIM validates connectivity:
      - GET /subscriptions?api-version=2022-12-01
  → Creates tenant record with onboarding_method = 'app_registration'
  → First ARI snapshot triggered
```

## Authentication Architecture

### Frontend → API

```
1. User authenticates via MSAL.js to MSP Entra ID
2. Token audience: api://{AIM_API_CLIENT_ID}
3. Token includes:
   - oid (user object ID)
   - tid (MSP tenant ID — always the MSP tenant)
   - roles (aim.admin, aim.operator, aim.auditor)
   - groups (optional, for team-based access)
4. API validates token:
   - Issuer: https://login.microsoftonline.com/{MSP_TENANT_ID}/v2.0
   - Audience: api://{AIM_API_CLIENT_ID}
   - Signature: via JWKS endpoint
5. API resolves user permissions:
   - Query user_tenant_access table for authorized tenants
   - If role = aim.admin: access to all tenants
   - If role = aim.operator: access to assigned tenants
   - If role = aim.auditor: read-only to assigned tenants
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

| Role | Scope | Permissions |
|---|---|---|
| `aim.admin` | Global | All tenants, user management, onboarding, settings |
| `aim.operator` | Per-tenant | View inventory, changes, recommendations; run AI queries; acknowledge findings |
| `aim.auditor` | Per-tenant | Read-only view of all data; export reports; no mutations |
| `customer.admin` | Own tenant | View own tenant data; manage own tenant preferences |
| `customer.viewer` | Own tenant | Read-only view of own tenant data |

## Data Isolation Enforcement Points

### Layer 1: API Gateway (APIM)
- JWT validation at gateway level
- Extract tenant_id from X-Tenant-Id header
- Validate tenant_id against user's authorized tenants claim

### Layer 2: Application Code
- Every API function sets SQL session context: `tenant_id`
- Every blob operation uses tenant-specific container
- Every Resource Graph query is scoped to tenant's subscriptions

### Layer 3: Database (RLS)
- Row-Level Security policy on every table
- Even direct SQL access (for debugging) requires setting session context
- DBA access requires explicit policy exemption

### Layer 4: Encryption
- Azure SQL TDE (transparent data encryption) — at rest
- TLS 1.2+ — in transit
- Column-level encryption for sensitive config values (connection strings)
- Key Vault-managed keys per tenant (optional, for strict compliance)

## Secrets Management

| Secret Type | Storage | Rotation |
|---|---|---|
| App Registration client secrets | Key Vault | Auto-rotate every 90 days |
| App Registration certificates | Key Vault | Auto-rotate every 12 months |
| Azure SQL connection string | Key Vault + Managed Identity (passwordless preferred) | N/A |
| OpenAI API key | Key Vault | Manual (single MSP key) |
| Service Bus connection | Managed Identity (no key) | N/A |
| Blob Storage connection | Managed Identity (no key) | N/A |

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
