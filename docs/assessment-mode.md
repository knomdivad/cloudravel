# Assessment Delivery Mode — Analysis & Build (t_de701540)

**Goal:** make David's 10-day fixed-fee Azure cost assessment ($3,000, risk-free =
double-the-fee-in-savings-over-12-months-or-free) cheap to deliver and repeatable
with CloudRavel as the delivery vehicle.

## What the assessment needs vs. what the platform had

| Assessment need | Platform before | Gap |
|---|---|---|
| Time-boxed engagement per customer | Tenant rows had no engagement concept | No way to mark "this workspace is an assessment" |
| Read-only during the engagement | AutoRemediationMode=Disabled was opt-in, and the remediation engine never re-checked tenant policy at approve/execute time | A hand-edited or auto-mode tenant could act on a customer estate |
| Ranked-by-dollars fix-list | Data existed (Advisor w/ `estimated_savings`, anomalies, Defender, Policy) but no aggregation, ranking, or export | The deliverable itself |
| Risk-free guarantee evidence | Dashboard summed Advisor savings ÷ 12 | No fee/target math, no client-facing document |
| Convert-to-monitoring upsell | Status lifecycle only (active/suspended/…) | No engagement transition |

## Scope chosen (prioritized by value-to-effort for 1–2 assessments/month)

**Built:**

1. **Engagement lifecycle on the workspace** (`engagement_kind` + 3 timestamp columns on `tenants`).
   `POST /api/assessment/start` (windowDays, fee) → watching; `POST /api/assessment/complete`
   → fix-list delivered; `POST /api/assessment/convert` → ongoing monitoring (restores
   Gated approval mode). State is one `GET /api/assessment` away. Migration
   `database/002-assessment.sql` is idempotent so existing dev volumes upgrade in place;
   fresh installs get the columns via 001-schema.sql.
2. **The fix-list** — `GET /api/assessment/report?format=json|csv|md`. Ranking lives in
   pure, unit-tested code (`Core/Models/AssessmentReportBuilder.cs`): Tier 1 quantified
   Advisor cost items by annual $ desc, Tier 2 unquantified cost items + open
   CostAnomaly anomalies, Tier 3 risk items. Summary carries estate stats (resources,
   last snapshot, changes in watch window) and the guarantee math. Markdown is the
   client-ready document; CSV feeds the spreadsheet; JSON feeds the UI.
3. **Risk-free guarantee support** — report carries
   `savings: {identifiedAnnual, fee, targetSavings=2×fee, meetsGuarantee, feeMultiple}`.
   The UI (new **Assessment** page) renders it as a progress card. The realized-savings
   *ledger* (what the customer actually realized) is deferred — it needs Cost Management
   integration — but the data model leaves a clean seam (the report builder takes any
   savings source; a ledger would add a second source alongside Advisor).
4. **Read-only enforcement, defense in depth** — starting an assessment forces
   `AutoRemediationMode=Disabled` **and** `AssessmentPolicy.BlocksActions()` is checked
   inside `RemediationService.ApproveAsync/ExecuteAsync`, so *no* path (inline approval,
   AI-proposal approval, the queue-drain worker) can act on an active assessment
   workspace even if a tenant row is hand-edited. Proposals stay allowed — they are
   inert fix-list raw material. Convert unlocks action.
5. **Frontend** — new Assessment page (state card, savings/guarantee/estate cards,
   ranked fix-list with tier badges, Markdown/CSV export buttons, start modal,
   complete/convert actions) + nav entry. Types and SWR hooks in lib/api + lib/hooks.

**Deferred (on purpose):** realized-savings ledger, PDF export, customer-facing sharing
portal, per-subscription scoping of the report. None block delivering an assessment.

## Why this shape

- **Zero new infrastructure.** No new timers, queues, or services. The watch window is
  observed by the existing snapshot cadence + 15-min change polling; the report is
  computed on read from the authoritative stores. Operational cost of the feature is 0
  when idle.
- **No new tenant kind.** Assessment is a mode of the existing workspace, so RLS,
  onboarding (Lighthouse / app-reg), inventory, and AIOps all work unchanged. A
  customer-owned, read-only estate is just a tenant with `AutoRemediationMode=Disabled`
  + the engine-side gate.
- **The report is pure code.** Ranking rules are the thing David is selling ("ranked by
  dollars"); they live in one testable file with no data access, so the rules can be
  tuned (e.g. different tiering, confidence weights) without touching I/O.

## How it makes delivery cheaper

Single consultant flow per engagement: connect customer cloud (existing Clouds flow) →
`POST /assessment/start` → let it watch 10 days (nothing to do) → open Assessment page →
download the Markdown report → paste into the client email → `POST /assessment/complete`
→ if the client signs, `POST /assessment/convert`. The report writing — historically the
hours-long part — is mechanical; David's time goes to the judgment calls the report
surfaces (which Tier-2 items are real, what the anomalies mean behind the bill).

## Verification (all run live, see scripts-dev/verify-assessment.sh)

- `dotnet build` 0 errors; `dotnet test` **35/35 pass** (23 existing + 12 new: ranking,
  guarantee math, tiering, markdown/CSV render, policy gate).
- `npx tsc --noEmit` clean; `next build` clean (`/assessment` route 3.86 kB).
- Live stack (`make up` on OrbStack): migration 002 applied (columns verified in SQL);
  full API flow exercised with real tokens: start → report JSON/CSV/MD with seeded
  $1,840 + $260 items ranked correctly ($2,100/yr = 0.70× fee → guarantee not yet met) →
  remediation approval **blocked** while watching → complete → convert → approval path
  restored (HTTP 200).
