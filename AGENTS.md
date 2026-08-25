# AGENTS.md

## Cursor Cloud specific instructions

CloudRavel is a multi-service platform. The supported local run path is **Docker
Compose** (see `README.md` → *Run the full stack with Docker*). Compose brings up
six services: `mssql` (Azure SQL Edge), `openbao` (secret store), `azurite`
(storage emulator), `migrator` (one-shot schema apply), `api` (.NET 8 Azure
Functions), and `web` (Next.js static export served by nginx). URLs, ports, and
the seeded dev login (`admin@local` / `ChangeMe123!`) are documented in `README.md`.

### Toolchain / prerequisites (not auto-installed by the update script)

The update script only refreshes project dependencies. These system tools are
required and must be present on the VM (install them if a fresh VM lacks them):

- **Docker Engine + compose plugin** — required to run the app stack. This VM is
  Docker-in-Docker: the daemon is **not** a managed service, so start it yourself
  and leave it running: `sudo dockerd > /tmp/dockerd.log 2>&1 &`. Docker 29 needs
  fuse-overlayfs with the containerd snapshotter disabled — `/etc/docker/daemon.json`
  should contain `{"storage-driver":"fuse-overlayfs","features":{"containerd-snapshotter":false}}`
  and iptables must be set to `iptables-legacy`.
- **.NET 8 SDK** — required to build/test the backend outside Docker. Installed at
  `~/.dotnet` and symlinked to `/usr/local/bin/dotnet`. If `dotnet` is missing,
  reinstall with `curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"`.
- **Node 20+/npm** — present in the base image; used for the frontend.

### Running the stack (non-obvious startup caveats)

1. `cp .env.example .env` if `.env` is missing. The example defaults work as-is
   (the `.env` is gitignored and not committed).
2. **OpenBao volume ownership gotcha (important):** on the *first* `docker compose up`,
   the fresh `*_openbao-data` named volume is created owned by `root`, but the
   OpenBao image runs as uid `100`, so it exits with code 2 and
   `mkdir /openbao/data/core: permission denied`, which fails the `api` startup
   dependency. Fix by chowning the volume once before/after creating it:
   ```
   docker compose up -d mssql openbao azurite
   sudo chown -R 100:1000 "$(docker volume inspect "$(basename "$PWD")_openbao-data" --format '{{.Mountpoint}}')"
   docker compose up -d openbao   # recreate; it becomes healthy
   docker compose up -d           # start the rest (api, web, migrator)
   ```
   (When invoking docker via `sudo dockerd`, run the `docker`/`docker compose`
   commands with `sudo` too.)
3. Verify: `curl http://localhost:7071/api/health` (direct) or
   `http://localhost:3000/api/health` (through the nginx proxy) should report
   `"status":"healthy"` with the database check passing.

### Lint / test / build (commands mirror `.github/workflows/build.yml`)

- **Backend** (repo root; needs `.NET 8 SDK`): `dotnet restore CloudRavel.sln`,
  `dotnet build CloudRavel.sln -c Release --no-restore`,
  `dotnet test CloudRavel.sln -c Release --no-build`. Tests live in
  `src/backend/CloudRavel.Tests` (xUnit, no external services required).
- **Frontend** (`src/frontend`): `npm run lint` (zero-warning gate),
  `npm run build` (static export to `out/`), `npm run test:help` (help-content
  check). Dev server: `npm run dev` (port 3000).

The `api` runtime image is the amd64-only Azure Functions base image; this VM is
amd64 so it runs natively (no emulation). `PLATFORM_ENVIRONMENT=Development`
(the compose default) disables live cloud collection, so no real cloud
credentials are needed to run and exercise the UI.
