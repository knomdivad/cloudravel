#!/usr/bin/env python3
"""Post one signed BotMonitor heartbeat (stdlib only: hmac, hashlib, urllib).

Credentials are read from 0600 files (preferred) or environment variables.
Never pass tokens or secrets on the command line.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import stat
import sys
import time
import urllib.error
import urllib.request
from typing import Tuple

API_BASE = os.environ.get("CR_API_BASE", "http://localhost:7071/api").rstrip("/")


def _require_mode_0600(path: str) -> None:
    mode = stat.S_IMODE(os.stat(path).st_mode)
    if mode != 0o600:
        raise PermissionError(f"Credential file must be mode 0600: {path}")


def load_secret(path: str) -> str:
    _require_mode_0600(path)
    with open(path, encoding="utf-8") as f:
        return f.read().strip()


def resolve_credentials() -> Tuple[str, str]:
    token_path = os.environ.get("CR_BOT_TOKEN_FILE")
    secret_path = os.environ.get("CR_BOT_HMAC_SECRET_FILE")
    if token_path and secret_path:
        return load_secret(token_path), load_secret(secret_path)
    if "CR_BOT_TOKEN" in os.environ and "CR_BOT_HMAC_SECRET" in os.environ:
        return os.environ["CR_BOT_TOKEN"].strip(), os.environ["CR_BOT_HMAC_SECRET"].strip()
    raise SystemExit(
        "Set CR_BOT_TOKEN_FILE and CR_BOT_HMAC_SECRET_FILE (mode 0600) "
        "or CR_BOT_TOKEN and CR_BOT_HMAC_SECRET in the environment."
    )


def sign_heartbeat_body(secret_b64: str, timestamp: int, body: bytes) -> str:
    secret = base64.b64decode(secret_b64)
    msg = f"{timestamp}.".encode("utf-8") + body
    return hmac.new(secret, msg, hashlib.sha256).hexdigest()


def load_optional_metrics() -> dict | None:
    path = os.environ.get("CR_BOT_METRICS_FILE")
    raw = os.environ.get("CR_BOT_METRICS")
    if path:
        _require_mode_0600(path)
        with open(path, encoding="utf-8") as f:
            raw = f.read()
    if not raw:
        return None
    parsed = json.loads(raw)
    if parsed is None:
        return None
    if not isinstance(parsed, dict):
        raise ValueError("Metrics JSON must be an object.")
    return parsed


def build_payload() -> dict:
    payload: dict = {
        "status": os.environ.get("CR_BOT_STATUS", "ok"),
        "lastTask": os.environ.get("CR_BOT_TASK", "Keep health routine"),
        "errors": [],
        "keepCheck": os.environ.get("CR_KEEP_CHECK", "pass"),
        "keepCheckDetail": os.environ.get("CR_KEEP_DETAIL", "Routine Keep checks completed"),
    }
    metrics = load_optional_metrics()
    if metrics:
        payload["metrics"] = metrics
    return payload


def post_heartbeat(token: str, secret_b64: str, payload: dict | None = None) -> int:
    body = json.dumps(payload or build_payload(), separators=(",", ":")).encode("utf-8")
    ts = int(time.time())
    sig = sign_heartbeat_body(secret_b64, ts, body)
    req = urllib.request.Request(
        f"{API_BASE}/bot-monitor/heartbeat",
        data=body,
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "X-CR-Timestamp": str(ts),
            "X-CR-Signature": sig,
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.status
    except urllib.error.HTTPError as exc:
        return exc.code


def main() -> None:
    token, secret_b64 = resolve_credentials()
    status = post_heartbeat(token, secret_b64)
    if status == 200:
        print("Heartbeat accepted")
        sys.exit(0)
    print(f"Heartbeat failed (HTTP {status})", file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    main()
