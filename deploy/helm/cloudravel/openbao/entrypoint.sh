#!/bin/sh
# Persistent single-node OpenBao for CloudRavel (file storage under /openbao/data).
#
# Optional OPENBAO_ROOT_TOKEN: after init/unseal, ensures a long-lived token with
# that id exists (full secret/* ACL) so compose/helm can keep a stable API token
# without sharing the PVC with the API pod.
#
# Token file (always written): $OPENBAO_DATA_DIR/.cloudravel-root-token
# Compose mounts the data volume read-only on the API as OpenBao__TokenFile.

set -eu

export BAO_ADDR="${BAO_ADDR:-http://127.0.0.1:8200}"
export VAULT_ADDR="$BAO_ADDR"
DATA_DIR="${OPENBAO_DATA_DIR:-/openbao/data}"
CONFIG="${OPENBAO_CONFIG:-/openbao/config/config.hcl}"
KEYS_FILE="${DATA_DIR}/.cloudravel-unseal-keys"
TOKEN_FILE="${DATA_DIR}/.cloudravel-root-token"
INIT_MARKER="${DATA_DIR}/.cloudravel-initialized"
POLICY_NAME="cloudravel"

mkdir -p "$DATA_DIR"

if command -v bao >/dev/null 2>&1; then
  CLI=bao
elif command -v vault >/dev/null 2>&1; then
  CLI=vault
else
  echo "Neither bao nor vault CLI found" >&2
  exit 1
fi

echo "Starting OpenBao ($CLI) with $CONFIG"
$CLI server -config="$CONFIG" &
SERVER_PID=$!
trap 'kill "$SERVER_PID" 2>/dev/null || true' EXIT INT TERM

i=0
while [ "$i" -lt 90 ]; do
  set +e
  $CLI status >/dev/null 2>&1
  st=$?
  set -e
  if [ "$st" -eq 0 ] || [ "$st" -eq 2 ]; then
    break
  fi
  i=$((i + 1))
  sleep 0.5
done

parse_init() {
  if command -v python3 >/dev/null 2>&1; then
    python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["unseal_keys_b64"][0]); print(d["root_token"])' <<EOF
$1
EOF
    return
  fi
  u=$(printf '%s' "$1" | tr -d '\n' | sed -n 's/.*"unseal_keys_b64"[[:space:]]*:[[:space:]]*\[[[:space:]]*"\([^"]*\)".*/\1/p')
  r=$(printf '%s' "$1" | tr -d '\n' | sed -n 's/.*"root_token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
  printf '%s\n%s\n' "$u" "$r"
}

ensure_app_token() {
  # $1 = root token for auth
  export BAO_TOKEN="$1"
  export VAULT_TOKEN="$1"

  # Full access policy for the CloudRavel API token.
  $CLI policy write "$POLICY_NAME" - <<'EOF' >/dev/null
path "secret/*" {
  capabilities = ["create", "read", "update", "delete", "list"]
}
path "secret/data/*" {
  capabilities = ["create", "read", "update", "delete", "list"]
}
path "secret/metadata/*" {
  capabilities = ["create", "read", "update", "delete", "list"]
}
EOF

  DESIRED="${OPENBAO_ROOT_TOKEN:-}"
  if [ -n "$DESIRED" ]; then
    # Best-effort fixed id for helm/compose secret openbao-token.
    if $CLI token create -id="$DESIRED" -policy="$POLICY_NAME" -orphan -display-name=cloudravel-api 2>/dev/null; then
      printf '%s\n' "$DESIRED" > "$TOKEN_FILE"
      echo "Ensured API token id=$DESIRED"
      return
    fi
    # Token id already exists — assume it is still valid.
    printf '%s\n' "$DESIRED" > "$TOKEN_FILE"
    echo "API token id=$DESIRED already present (reusing)"
    return
  fi

  # No fixed id requested: create (or keep) a random long-lived orphan token in TOKEN_FILE.
  if [ -f "$TOKEN_FILE" ] && [ -s "$TOKEN_FILE" ]; then
    # Keep existing app token across restarts when no OPENBAO_ROOT_TOKEN is set.
    return
  fi
  NEW=$($CLI token create -policy="$POLICY_NAME" -orphan -display-name=cloudravel-api -format=json \
    | tr -d '\n' | sed -n 's/.*"client_token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
  if [ -z "$NEW" ]; then
    # Fall back to root token if create failed
    printf '%s\n' "$1" > "$TOKEN_FILE"
  else
    printf '%s\n' "$NEW" > "$TOKEN_FILE"
  fi
  chmod 600 "$TOKEN_FILE"
}

if [ ! -f "$INIT_MARKER" ]; then
  echo "First boot: initializing OpenBao..."
  INIT_OUT=$($CLI operator init -key-shares=1 -key-threshold=1 -format=json)
  PARSED=$(parse_init "$INIT_OUT")
  UNSEAL=$(printf '%s\n' "$PARSED" | sed -n '1p')
  ROOT=$(printf '%s\n' "$PARSED" | sed -n '2p')
  if [ -z "$UNSEAL" ] || [ -z "$ROOT" ]; then
    echo "Could not parse operator init output:" >&2
    printf '%s\n' "$INIT_OUT" >&2
    exit 1
  fi
  printf '%s\n' "$UNSEAL" > "$KEYS_FILE"
  printf '%s\n' "$ROOT" > "${DATA_DIR}/.cloudravel-init-root"
  chmod 600 "$KEYS_FILE" "${DATA_DIR}/.cloudravel-init-root"

  $CLI operator unseal "$UNSEAL" >/dev/null
  export BAO_TOKEN="$ROOT"
  export VAULT_TOKEN="$ROOT"
  $CLI secrets enable -path=secret -version=2 kv 2>/dev/null || \
    $CLI secrets enable -path=secret kv-v2 2>/dev/null || true
  ensure_app_token "$ROOT"
  touch "$INIT_MARKER"
  echo "Initialized. API token file: $TOKEN_FILE"
else
  echo "Unsealing existing store..."
  UNSEAL=$(cat "$KEYS_FILE")
  $CLI operator unseal "$UNSEAL" >/dev/null
  ROOT=$(cat "${DATA_DIR}/.cloudravel-init-root" 2>/dev/null || true)
  if [ -z "$ROOT" ]; then
    # Older data dirs: fall back to token file as auth for policy ensure
    ROOT=$(cat "$TOKEN_FILE" 2>/dev/null || true)
  fi
  if [ -n "$ROOT" ]; then
    ensure_app_token "$ROOT"
  fi
fi

echo "OpenBao ready (file storage under $DATA_DIR)"
wait "$SERVER_PID"
