# Contributing to CloudRavel

Thanks for taking the time. This document covers what you need to get a change
merged without guessing.

**Security problems do not belong in issues or pull requests.** See
[SECURITY.md](SECURITY.md) for the private reporting channel.

## Toolchain

Versions are pinned in three places that must agree: `global.json`,
`.github/workflows/build.yml`, and `.cursor/install.sh`.

| Tool | Version |
|---|---|
| .NET SDK | 8.0 |
| Node | 20 (see `.nvmrc`) |
| Terraform | 1.9.x |
| Helm | 3.16.x |
| Docker | any recent version, for the local stack |

`.cursor/install.sh` installs all four and is safe to run directly on a Linux
machine if you would rather not install them by hand.

## Running the stack

```bash
cp .env.example .env
# Set MSSQL_SA_PASSWORD and LOCAL_AUTH_JWT_SIGNING_KEY at minimum.
make up
```

Web UI on <http://localhost:3000>, API on <http://localhost:7071/api>. The
seeded login is `admin@local` / `ChangeMe123!`, which is for local use only.

`make logs` follows output, `make down` tears it down. The README covers running
the backend and frontend directly without Docker.

## Before you open a pull request

Run what CI runs. All of it is fast except the container builds.

```bash
# Backend
dotnet restore CloudRavel.sln --locked-mode
dotnet format CloudRavel.sln --verify-no-changes
dotnet build CloudRavel.sln --configuration Release --no-restore
dotnet test CloudRavel.sln --configuration Release --no-build

# Frontend
cd src/frontend
npm ci
npm run lint
npm run typecheck
npm run build

# Infrastructure
terraform -chdir=infra/terraform fmt -check -recursive
terraform -chdir=infra/terraform init -backend=false
terraform -chdir=infra/terraform validate
helm lint deploy/helm/cloudravel \
  --set secrets.mssqlSaPassword=x --set secrets.localAuthJwtSigningKey=y
```

Two of these fail in ways worth explaining in advance:

**`dotnet format` fails.** Run it without `--verify-no-changes` to apply the
fix. `.editorconfig` encodes the style the codebase already uses; if you
disagree with a rule, change it there and say why rather than working around it.

**Restore fails with `NU1004`.** You changed a package reference without
updating the lock file. Regenerate it:

```bash
dotnet restore CloudRavel.sln --force
git add src/backend/**/packages.lock.json
```

Locked mode is what stops a transitive dependency from moving without anyone
noticing, so please do not disable it.

## Tests

New tests go in `src/backend/CloudRavel.Tests`, mirroring the namespace of what
they cover. xUnit with NSubstitute for fakes; `TestSupport/FunctionsTestHarness`
provides the isolated-worker HTTP doubles the SDK does not ship.

Coverage is measured but not yet enforced, so use judgement rather than chasing
a number. What we care about most:

- **Authorization.** Any endpoint or helper deciding who may do what. Test the
  denial as well as the success, and confirm the underlying service is never
  reached on denial.
- **The remediation path.** Anything touching the approval gate or execution
  state machine can change a customer's live infrastructure.
- **Tenant isolation.** Anything carrying a `tenantId` into a query.

For a bug fix, a test that fails before your change and passes after is worth
more than several that only pass after. Say so in the PR when you have one.

## Style

Match the file you are editing. Beyond what `.editorconfig` and ESLint enforce:

- Comments explain *why*, not *what*. If a line needs a comment to say what it
  does, the line is usually the problem. Constraints, trade-offs, and
  non-obvious ordering are worth writing down.
- Do not leave commented-out code. Git remembers it.
- Follow the existing error shape: a `code` and a `message`, with the code
  stable enough for the frontend to switch on.

## Commits and pull requests

Write the subject in the imperative and under about 72 characters — "Require
cloud_admin for AI-proposed remediations", not "fixed auth bug". Use the body
for why the change is needed and what a reviewer should watch out for.

Keep one logical change per commit. A pull request may contain several, but a
reviewer should be able to read them in order and follow the reasoning.

The pull request template asks what changed, why, and how you verified it. That
last part is the one people skip and the one reviewers most need.

## Licensing

CloudRavel is licensed under the **GNU Affero General Public License v3.0**. By
contributing you agree your work is distributed under the same terms. AGPL's
network clause means anyone offering CloudRavel as a hosted service must make
their source available to its users — worth understanding before you build on it
commercially.
