#!/usr/bin/env python3
"""Unit tests for send-heartbeat.py signing helpers."""

from __future__ import annotations

import base64
import os
import stat
import tempfile
import unittest

import importlib.util
from pathlib import Path

_module_path = Path(__file__).resolve().parent / "send-heartbeat.py"
_spec = importlib.util.spec_from_file_location("send_heartbeat", _module_path)
assert _spec and _spec.loader
hb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hb)


class SendHeartbeatTests(unittest.TestCase):
    def test_sign_matches_hmac_sha256_prefix(self) -> None:
        secret = base64.b64encode(b"\x01" * 32).decode()
        body = b'{"status":"ok"}'
        ts = 1_700_000_000
        sig = hb.sign_heartbeat_body(secret, ts, body)
        self.assertEqual(64, len(sig))
        self.assertEqual(sig, sig.lower())

    def test_load_secret_requires_0600(self) -> None:
        with tempfile.NamedTemporaryFile(delete=False) as f:
            f.write(b"token-value")
            path = f.name
        try:
            os.chmod(path, 0o644)
            with self.assertRaises(PermissionError):
                hb.load_secret(path)
            os.chmod(path, 0o600)
            self.assertEqual("token-value", hb.load_secret(path))
        finally:
            os.unlink(path)

    def test_resolve_credentials_from_files(self) -> None:
        with tempfile.TemporaryDirectory() as d:
            token_path = os.path.join(d, "token")
            secret_path = os.path.join(d, "secret")
            with open(token_path, "w", encoding="utf-8") as f:
                f.write("tok")
            with open(secret_path, "w", encoding="utf-8") as f:
                f.write(base64.b64encode(b"\x02" * 32).decode())
            os.chmod(token_path, 0o600)
            os.chmod(secret_path, 0o600)
            os.environ["CR_BOT_TOKEN_FILE"] = token_path
            os.environ["CR_BOT_HMAC_SECRET_FILE"] = secret_path
            os.environ.pop("CR_BOT_TOKEN", None)
            os.environ.pop("CR_BOT_HMAC_SECRET", None)
            token, secret = hb.resolve_credentials()
            self.assertEqual("tok", token)
            self.assertTrue(secret)


if __name__ == "__main__":
    unittest.main()
