# CloudRavel Helm chart

Deploys CloudRavel — API, web UI, and optionally the SQL, blob storage, and
secret-store dependencies — onto a Kubernetes cluster.

## Install

```bash
helm install cloudravel oci://ghcr.io/knomdivad/charts/cloudravel \
  --set secrets.mssqlSaPassword='<strong password>' \
  --set secrets.localAuthJwtSigningKey='<long random value>'
```

Both are required; the render fails without them. Supply your own Secret
instead with `secrets.existingSecret`, containing the keys
`mssql-sa-password`, `local-auth-jwt-signing-key`, `openbao-token`,
`openai-api-key`, and `azure-webjobs-storage`.

Then check the release is actually wired up:

```bash
helm test cloudravel
```

That verifies the web pod serves, the API reports ready (which means it reached
the database and found a schema), and the backing services accept connections.

## Read this before production

The defaults stand a working stack up on a bare cluster. They are not a
production posture.

| Setting | Default | Change it to |
|---|---|---|
| `ingress.tls.enabled` | `false` | `true`, with `tls.secretName` |
| `api.corsAllowedOrigins` | `http://localhost:3000` | your real UI origin |
| `api.platformEnvironment` | `Development` | `Production` for live collection |
| `networkPolicy.enabled` | `false` | `true` on a CNI that enforces it |
| bundled `mssql`/`openbao` | enabled | disable, point at managed services |

The seeded application login (`admin@local` / `ChangeMe123!`) is public — its
hash is in source control. Change it on first sign-in. See
[SECURITY.md](../../../SECURITY.md).

## Architecture

```
ingress → web (nginx, static export, proxies /api)
              └→ api (.NET 8 Functions isolated worker)
                    ├→ mssql    or externalMssql
                    ├→ azurite  or externalStorage
                    └→ openbao  or externalOpenBao
```

The migration Job runs as a `post-install` / `post-upgrade` hook. The API's
readiness probe returns 200 only after that Job has created the schema, so
traffic is gated on migration rather than racing it.

## Using managed services

Disable a bundled dependency and fill its `external*` block. The chart fails the
render if the matching external value is missing, rather than deploying
something that cannot connect.

```yaml
mssql:
  enabled: false
externalMssql:
  host: sql.example.com
  port: 1433
  database: cloudraveldb
  user: cloudravel
```

The same shape applies to `azurite` / `externalStorage.connectionString` and
`openbao` / `externalOpenBao.address`.

## Security contexts

`podSecurityContext` and `containerSecurityContext` at the top level are the
chart-wide defaults; every component can override them under its own key, and
the two are merged with the component winning.

The defaults are what holds for every image: `allowPrivilegeEscalation: false`,
`privileged: false`, all capabilities dropped, and the `RuntimeDefault` seccomp
profile. `runAsNonRoot` and `readOnlyRootFilesystem` are set per component,
because they depend on what each image actually does.

Where a component deviates, it is because the image requires it. These were
each found by running the chart, not by reading documentation:

| Component | Deviation | Why |
|---|---|---|
| `web` | runs as uid 101, `readOnlyRootFilesystem`, `fsGroup: 101` | As root, nginx chowns its cache directory at startup and needs `CAP_CHOWN`. Running as the image's own nginx user avoids handing back CHOWN, SETUID, SETGID, and DAC_OVERRIDE. `fsGroup` is what makes the emptyDir volumes writable. |
| `mssql` | `runAsGroup: 0`, adds `NET_BIND_SERVICE` | mssql is uid 10001 but **gid 0**, and reaches `/var/opt/mssql` through group root. Separately, `sqlservr` carries the file capability `cap_net_bind_service+ep`, and the kernel refuses to exec it when that cannot be raised — dropping ALL without adding it back fails with a bare `Operation not permitted`. |
| `api` | root, adds `NET_BIND_SERVICE` | The Functions host listens on :80 and writes to paths a read-only root filesystem would need mapped first. |
| `openbao` | adds `IPC_LOCK` | Lets OpenBao mlock its memory so secrets are not paged to disk. |

### On binding port 80

`web` binds :80 as uid 101 because kubelet defaults
`net.ipv4.ip_unprivileged_port_start` to 0 — not because of `NET_BIND_SERVICE`.
A non-root process gets no effective capabilities from `capabilities.add`
(measured: `CapEff` is 0). On a cluster that raises that sysctl, move nginx to
:8080 and repoint the Service; adding the capability will not help.

## Network policy

Off by default. On a cluster whose CNI does not enforce NetworkPolicy these
objects are accepted and silently ignored, which is worse than not having them
because it looks like protection.

Enabled, the database, storage, and secret store accept traffic only from this
release's own pods, and `api` and `web` accept ingress-controller traffic.
Egress stays unrestricted, because the API must reach Azure, AWS, GCP, and the
AI provider.

## Disruption budgets

Off by default. With `replicaCount: 1` a budget of `minAvailable: 1` makes the
pod undrainable and blocks every node drain and cluster upgrade. Enable it once
a component runs more than one replica; the template also requires that, so
enabling it alone cannot wedge maintenance.

The stateful components have none. They are single-replica with ReadWriteOnce
volumes and a `Recreate` strategy, so a budget could only prevent maintenance,
never preserve availability.

## Values

`values.schema.json` validates on install, upgrade, lint, and template. It is
permissive about additions and strict about shapes that otherwise fail late:
wrong types, unknown `openbao.mode` or `platformEnvironment` values, malformed
storage sizes, and out-of-range ports.

See [values.yaml](values.yaml) for the full set, each with a comment.
