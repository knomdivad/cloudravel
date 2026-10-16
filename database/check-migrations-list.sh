#!/usr/bin/env bash
# CI guard: every name in apply-migrations.sh MIGRATIONS must have a .sql file,
# and every numbered migration .sql in database/ must appear in MIGRATIONS (or EXCLUDED).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPLY="${SCRIPT_DIR}/apply-migrations.sh"

if [[ ! -f "${APPLY}" ]]; then
  echo "ERROR: missing ${APPLY}" >&2
  exit 1
fi

# Utility / manual scripts — not applied by the migrator ledger.
EXCLUDED=(
  repair-org-workspace-shell
  reset-admin-password
  seed-demo-data
  seed-bot-monitor-demo
  test-006-bot-monitor-min-schema
)

mapfile -t MIGRATIONS < <(
  awk '/^MIGRATIONS=\(/,/^\)/ {
    if ($0 ~ /^[[:space:]]+[0-9]{3}-/ && $0 !~ /^[[:space:]]*#/) {
      gsub(/[^a-z0-9-]/, "", $1)
      print $1
    }
  }' "${APPLY}"
)

if [[ "${#MIGRATIONS[@]}" -eq 0 ]]; then
  echo "ERROR: could not parse MIGRATIONS from ${APPLY}" >&2
  exit 1
fi

errors=0

for m in "${MIGRATIONS[@]}"; do
  f="${SCRIPT_DIR}/${m}.sql"
  if [[ ! -f "${f}" ]]; then
    echo "ERROR: MIGRATIONS lists '${m}' but ${f} is missing" >&2
    errors=$((errors + 1))
  fi
done

is_excluded() {
  local name=$1
  for x in "${EXCLUDED[@]}"; do
    [[ "${x}" == "${name}" ]] && return 0
  done
  return 1
}

is_listed() {
  local name=$1
  for m in "${MIGRATIONS[@]}"; do
    [[ "${m}" == "${name}" ]] && return 0
  done
  return 1
}

for f in "${SCRIPT_DIR}"/[0-9][0-9][0-9]-*.sql; do
  [[ -e "${f}" ]] || continue
  base=$(basename "${f}" .sql)
  if is_excluded "${base}"; then
    continue
  fi
  if ! is_listed "${base}"; then
    echo "ERROR: migration file ${f} is not in MIGRATIONS (add it or EXCLUDED in check-migrations-list.sh)" >&2
    errors=$((errors + 1))
  fi
done

if [[ "${errors}" -ne 0 ]]; then
  echo "Migration list check failed (${errors} issue(s))." >&2
  exit 1
fi

echo "Migration list check passed (${#MIGRATIONS[@]} migrations, ${#EXCLUDED[@]} excluded utility scripts)."
