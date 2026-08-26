<!--
Security fixes do not belong here while they are unfixed. See SECURITY.md for
the private channel.
-->

## What this changes

<!-- One or two sentences. What is different after this merges? -->

## Why

<!-- The problem, not the patch. Link an issue if there is one: Fixes #123 -->

## How it was verified

<!--
The section reviewers most need and contributors most often skip. Actual
commands and their outcome, not "tested locally". For a bug fix, the strongest
evidence is a test that fails before the change and passes after — say so if
you have one.
-->

## Risk

<!--
Anything a reviewer should look at twice: behaviour someone may depend on,
migrations, permission changes, config that must move with this.
Write "none" if there genuinely is none.
-->

---

- [ ] `dotnet format --verify-no-changes`, build, and tests pass
- [ ] Frontend `lint`, `typecheck`, and `build` pass (if touched)
- [ ] `terraform validate` / `helm lint` pass (if touched)
- [ ] Tests added or updated for the behaviour changed
- [ ] `CHANGELOG.md` updated under `[Unreleased]` for anything user-visible
- [ ] Lock files regenerated if dependencies moved (`dotnet restore --force`, `npm install`)

<!--
Touching authorization, tenant scoping, remediation, or credential handling?
Say which case you tested for denial, not only for success.
-->
