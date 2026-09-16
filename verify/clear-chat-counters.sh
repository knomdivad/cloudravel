#!/usr/bin/env bash
# Clear the shared chat_usage_counters rows (scratch-stack ops hook).
# Usage: bash verify/clear-chat-counters.sh [mssql-container]
set -euo pipefail
CONTAINER="${1:-t_331e0542-mssql-1}"
SQL="DELETE FROM chat_usage_counters WHERE counter_key LIKE 'chat|%';
SELECT COUNT(*) AS remaining FROM chat_usage_counters;"
docker exec -i "$CONTAINER" /bin/bash -c \
  'isql -v -k "Driver=/opt/microsoft/msodbcsql17/lib64/libmsodbcsql-17.7.so.2.1;Server=localhost;Database=cloudraveldb;UID=sa;PWD=$MSSQL_SA_PASSWORD;Encrypt=no"' \
  <<< "$SQL" | tail -5
