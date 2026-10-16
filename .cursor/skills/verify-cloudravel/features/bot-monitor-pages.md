# bot-monitor-pages

Capture operator and customer BotMonitor routes after seeding demo rows.

## Preconditions

- Stack is up (`control-cloudravel doctor` exits 0).
- BotMonitor migration applied (migrator includes `005-bot-monitor`).
- Demo seed loaded into SQL:

```bash
source /opt/cursor/artifacts/verify-cloudravel/state/run.env
sudo docker compose -p "$COMPOSE_PROJECT_NAME" exec -T mssql \
  /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d cloudraveldb -I \
  -i /database/seed-bot-monitor-demo.sql
```

## Drive

1. `control-cloudravel browser-login`
2. Open `/ops/bots` and screenshot `ops-bots.png`
3. Select organization `cloudravel-internal` in the sidebar switcher
4. Open `/portal/bots` and screenshot `portal-bots.png`

Artifacts under `/opt/cursor/artifacts/verify-cloudravel/<VERIFY_RUN_ID>/bot-monitor-pages/`.
