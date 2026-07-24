# syntax=docker/dockerfile:1
# ============================================================================
# Database migrator image — the same apply-migrations.sh the compose `migrator`
# service runs, but with the SQL files baked in so it needs no repo mount. Used
# as the Kubernetes migration Job (a Helm post-install/post-upgrade hook).
#
# Build context is database/ (COPY . brings in the *.sql files + the script).
# Uses the classic ODBC-based sqlcmd (mssql-tools), which tolerates SQL Server's
# self-signed cert — go-sqlcmd's stricter x509 parser rejects it.
#
# Runtime env: MSSQL_SA_PASSWORD (required), DB_HOST (defaults to `mssql`).
# ============================================================================
FROM --platform=linux/amd64 mcr.microsoft.com/mssql-tools:latest

# Strip any CR from a CRLF checkout so bash accepts the script, then run it.
COPY . /database/
RUN tr -d '\015' < /database/apply-migrations.sh > /usr/local/bin/apply-migrations.sh \
    && chmod +x /usr/local/bin/apply-migrations.sh

WORKDIR /database
ENTRYPOINT ["/usr/local/bin/apply-migrations.sh"]
