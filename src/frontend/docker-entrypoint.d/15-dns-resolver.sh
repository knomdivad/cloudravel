#!/bin/sh
# Derive nginx's DNS resolver from the container's resolv.conf so the same
# image works on Docker (127.0.0.11), Podman (network gateway), and k8s
# (CoreDNS). Used by default.conf.template's "resolver ${DNS_RESOLVER}".
set -eu

if [ -n "${DNS_RESOLVER:-}" ]; then
  exit 0
fi

ns=
if [ -r /etc/resolv.conf ]; then
  ns=$(awk '/^nameserver[[:space:]]+/ { print $2; exit }' /etc/resolv.conf || true)
fi

# Fallbacks: Docker embedded DNS, then a common Podman gateway.
export DNS_RESOLVER="${ns:-127.0.0.11}"
echo "nginx DNS resolver: ${DNS_RESOLVER}"
