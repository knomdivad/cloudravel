#!/usr/bin/env python3
"""Post one signed BotMonitor heartbeat. Reads CR_BOT_TOKEN and CR_BOT_HMAC_SECRET from the environment."""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import sys
import time
import urllib.error
import urllib.request

API_BASE = os.environ.get("CR_API_BASE", "http://localhost:7071/api").rstrip("/")
TOKEN = os.environ["CR_BOT_TOKEN"]
SECRET_B64 = os.environ["CR_BOT_HMAC_SECRET"]

payload = {
    "status": os.environ.get("CR_BOT_STATUS", "ok"),
    "lastTask": os.environ.get("CR_BOT_TASK", "Keep health routine"),
    "errors": [],
    "keepCheck": os.environ.get("CR_KEEP_CHECK", "pass"),
    "keepCheckDetail": os.environ.get("CR_KEEP_DETAIL", "Routine Keep checks completed"),
}
body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
ts = int(time.time())
secret = base64.b64decode(SECRET_B64)
msg = f"{ts}.".encode("utf-8") + body
sig = hmac.new(secret, msg, hashlib.sha256).hexdigest()

req = urllib.request.Request(
    f"{API_BASE}/bot-monitor/heartbeat",
    data=body,
    method="POST",
    headers={
        "Authorization": f"Bearer {TOKEN}",
        "Content-Type": "application/json",
        "X-CR-Timestamp": str(ts),
        "X-CR-Signature": sig,
    },
)

try:
    with urllib.request.urlopen(req, timeout=30) as resp:
        if resp.status == 200:
            print("Heartbeat accepted")
            sys.exit(0)
        print(f"Heartbeat failed (HTTP {resp.status})", file=sys.stderr)
        sys.exit(1)
except urllib.error.HTTPError as exc:
    print(f"Heartbeat failed (HTTP {exc.code})", file=sys.stderr)
    sys.exit(1)
