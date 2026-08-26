# Changelog

All notable changes to CloudRavel are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
the project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Two things belong in every entry that has them: a **Security** section when a
release changes the security posture, and an explicit note when an entry changes
behaviour someone may already depend on.

## [Unreleased]

## [1.0.0] — 2026-08-26

First versioned release. Marks the security, CI, test, governance, and Helm
hardening stack as the baseline for anyone deploying or forking from here.

### Security

- Proposing a remediation through the AI assistant now requires the
  `cloud_admin` role, matching `POST /api/remediations`. Previously
  `POST /api/ai/query` performed no authorization check, so any workspace
  member — including `read_only` — could propose remediations through the
  assistant's `propose_remediation` tool. Where a tenant had
  `AutoRemediationMode = Auto` and the playbook was low risk, that reached live
  cloud infrastructure. **Behaviour change:** read-only users who relied on this
  will now receive a permission error.
- AI-proposed remediations are attributed to `ai:query:<actor>` rather than a
  bare `ai:query`, so approvers can see who drove the model.
- Added a security scanning pipeline: CodeQL, dependency audits, gitleaks,
  Checkov, and Trivy. Findings report to the Security tab rather than failing
  builds while the existing backlog is worked down.
- Helm chart defaults now include restricted `securityContext`, a chart
  ServiceAccount, NetworkPolicy, and PodDisruptionBudget; OpenBao token
  handling fails closed when the token is missing.
- Documented in [SECURITY.md](SECURITY.md): `infra/terraform/tfplan.json` was
  committed in `f746912` and removed in `d85450f`, but the blob remains
  reachable from git history and contains a real SQL administrator password.
  Rotate that credential before any production deploy that ever used it.
- Frontend dependency patches (lodash, flatted, js-yaml, brace-expansion,
  picomatch, postcss) and Next.js `14.2.28` → `14.2.35`. Remaining Next 14
  advisories that only clear on a major upgrade are deferred.

### Added

- Pull request CI. Build, tests, lint, typecheck, format, Terraform validation,
  and Helm linting now run before merge rather than after.
- Test foundation: NSubstitute, coverlet, and doubles for the isolated-worker
  HTTP types, followed by coverage of the authorization ladder, the remediation
  approval gate and execution state machine, and AWS SigV4 signing. 23 tests to 96.
- Dependabot for NuGet, npm, GitHub Actions, and Docker, with semver-major
  ignores for framework and runtime base-image migrations.
- Container images are signed with keyless cosign and carry SPDX SBOM
  attestations, both addressed by digest.
- `.cursor/environment.json` and an install script pinning the .NET, Node,
  Terraform, and Helm versions the project builds with.
- Project governance: this changelog, `SECURITY.md`, `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, `CODEOWNERS`, and issue and pull request templates.
- Helm `values.schema.json`, chart README, and connection smoke tests.

### Changed

- All GitHub Actions are pinned to commit SHAs rather than floating tags.
- NuGet restores run in locked mode against per-project `packages.lock.json`,
  so a transitive dependency cannot change without the lock file changing too.
- `.editorconfig` added and using directives normalized, so `dotnet format`
  can be enforced in CI.
- Local login aborts after 20s so a slow API cannot wedge the sign-in button.
- Sidebar version label shows the commit hash; `/admin` hides org-scoped chrome;
  `make` auto-exports `GIT_SHA` for local image stamps.
- Helm chart version bumped to `0.3.0` (app `1.0.0`).

### Fixed

- Corrected the stale `home` and `sources` URLs in the Helm chart, which still
  pointed at the project's former name.
- Regenerated `CloudRavel.Tests/packages.lock.json` so locked-mode restore
  includes NSubstitute and coverlet.

### Removed

- `src/frontend/tsconfig.tsbuildinfo` is no longer tracked; it is a build
  artifact regenerated on every typecheck.

---

## Prior history

Releases were not tracked before this file existed. The notable work in the
repository up to that point, reconstructed from git history:

### Multi-cloud

- AWS and GCP reached parity with Azure across security, governance, and
  approvals.
- AWS inventory expanded past the Tagging API to cover VPC networking and EC2.
- Cloud provider inference corrected so Operations stopped labelling GCP
  resources as Azure, with a backfill for already-stored anomalies.
- Human-readable resource names preferred for AWS and GCP inventory.

### AIOps

- Anomaly detectors, an incident queue with SLA tracking, and a gated
  remediation approval workflow.
- Cloud hierarchy and provider badges surfaced across inventory, dashboard,
  changes, operations, approvals, security, and governance.

### Platform

- AI provider errors surfaced with actionable messages instead of opaque 500s.
- `cloud_admin` gained credential rotation and delete for clouds and orgs.
- Email established as the unique login identity, with local admin login
  hardened across upgrades.
- OpenBao secrets persisted across container recreation, preferring the token
  file over a stale environment variable.
- Database migrations collapsed into a single schema file.
- AGPLv3 license added.

[Unreleased]: https://github.com/knomdivad/cloudravel/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/knomdivad/cloudravel/releases/tag/v1.0.0
