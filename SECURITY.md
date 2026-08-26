# Security Policy

CloudRavel holds credentials for other people's cloud estates and can execute
changes against them. A defect here is not confined to this repository, so
please treat reports accordingly.

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report privately through GitHub Security Advisories:

<https://github.com/knomdivad/cloudravel/security/advisories/new>

That creates a private thread visible only to maintainers, and it is the only
channel we can promise is monitored. If advisories are unavailable to you,
contact the repository owner directly through their GitHub profile and say only
that you have a security report — do not include details in a public message.

Useful things to include, to the extent you have them:

- What an attacker gains: data read, privilege gained, resources changed.
- Affected version or commit, and the deployment path (Compose, Helm, Azure).
- Whether authentication is required, and at which role.
- Reproduction steps or a proof of concept.

### What to expect

This is a small project without a staffed security team. We aim to acknowledge
a report within a few days and to keep you informed as we work, rather than
committing to a fix deadline we cannot honour.

If you would like credit in the advisory, say so and tell us how to name you.
We will not disclose your report publicly before a fix, unless you ask us to.

## Scope

**In scope**

- Cross-tenant data access, in either the API or the database's row-level security.
- Authentication bypass, or privilege escalation between `read_only`,
  `cloud_admin`, `org_admin`, and `system_admin`.
- Any path that executes a cloud remediation without the approval the tenant's
  policy requires.
- Leakage of stored cloud credentials from OpenBao or Key Vault.
- Injection, SSRF, or deserialization flaws in the API.
- Making the AI assistant act outside the caller's own permissions.

**Out of scope**

- The documented development defaults below.
- Findings that require an already-compromised host or database.
- Automated scanner output with no demonstrated impact.
- Denial of service through sheer request volume.

## Known issues

These are already public. Reporting them again is not necessary, though a new
exploitation path for any of them is worth telling us about.

### A credential is exposed in git history

`infra/terraform/tfplan.json` was committed in `f746912` and removed in
`d85450f`. The path is gitignored now and the file is absent from `HEAD`, but
**the blob remains reachable from history** and contains a real SQL
administrator password.

Anyone who has deployed from this repository should treat that credential as
compromised and rotate it. Clearing the history requires a rewrite coordinated
across every clone and fork; rotating the credential is what actually removes
the risk, and must happen first.

### Development defaults are not safe for production

The local stack ships intentionally weak values so `make up` works with no
configuration. They are documented in the README's *Known limitations*:

| Default | Where | Must change before |
|---|---|---|
| `admin@local` / `ChangeMe123!` | seeded local admin | any shared environment |
| OpenBao token `root` | dev-mode OpenBao | any shared environment |
| `dev-only-change-me-in-production` | `LOCAL_AUTH_JWT_SIGNING_KEY` | any shared environment |
| Azurite well-known account key | local blob emulator | not used outside local |

### Other current limitations

- **Organization SSO is stored but not enforced.** Settings persist and the UI
  reflects them, but login federation is not applied
  (`enforcementStatus: not_implemented`). Do not rely on it as a control.
- **Login rate limiting is per-process.** With more than one API instance an
  attacker gets one bucket per instance. A shared store is needed for real
  protection.
- **Local JWTs last four hours** by default, configurable through
  `LocalAuth:TokenLifetimeHours`. There is no revocation list, so a token
  remains valid for its lifetime after a user is disabled.

## Deploying this safely

`docs/security-model.md` describes the intended model in full: tenant
onboarding, the four isolation layers, the RBAC ranking, and the remediation
safety model. At minimum, before any environment that is not your laptop:

1. Replace every default in the table above.
2. Supply a real secret store — Key Vault, or OpenBao outside dev mode — rather
   than the bundled dev instance.
3. Enable TLS. The Helm chart ships with `ingress.tls.enabled: false`.
4. Leave `AutoRemediationMode` at `Gated` or `Disabled` until you have watched
   what the detectors propose in your own estate. `Auto` executes low-risk
   playbooks against live infrastructure without a human.
5. Give cloud credentials the narrowest role that works. The platform can only
   act where you have let it.
