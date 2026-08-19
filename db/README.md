# Database Migrations

Forward-only, numbered SQL files. Each migration is idempotent (guarded by
`IF NOT EXISTS`) so the runner can be safely re-run.

## Layout

```
migrations/        DDL — applied in lexicographic order by filename
stored-procedures/ Application-owned procs (re-created via CREATE OR ALTER on every run)
triggers/          DB-level triggers (re-created via CREATE OR ALTER on every run)
```

Stored procedures and triggers use `CREATE OR ALTER` so they always reflect
the current contents of the file at the time the migration runner executes.
Migrations proper (under `migrations/`) only ever run once per environment —
their `IF NOT EXISTS` guard is just defence in depth.

## Applying Migrations

The .NET migration runner (`src/ApiGateway.Infrastructure/Migration/MigrationRunner.cs`)
discovers scripts via the assembly's `ManifestResourceString` for embedded
files, or via the file system path passed at startup. In production the
runner executes once on app startup before the host starts serving traffic;
in dev it runs on `dotnet run`.

Manual application against a dev SQL Server:

```bash
# Apply in order
for f in db/migrations/*.sql db/stored-procedures/*.sql db/triggers/*.sql; do
    sqlcmd -S localhost -d ApiGateway -U sa -P "$SA_PASSWORD" -i "$f"
done
```

## Adding a Migration

1. Pick the next number (e.g. `0009_...`).
2. Name the file `<number>_<short_description>.sql` in `migrations/`.
3. Wrap DDL in `IF NOT EXISTS (...) BEGIN ... END GO` so the runner can
   be re-run on a partially-applied schema without erroring.
4. For data backfills, prefer an idempotent `UPDATE ... WHERE NOT EXISTS`
   pattern over `INSERT` so re-runs don't duplicate rows.

## Partition Rotation for AuditLog_Metrics

`AuditLog_Metrics` is partitioned monthly by `OccurredAt`. The partition
rotation job (run via a scheduled Lambda — see `infra/terraform/monitoring/`)
pre-creates the next month's partition before the boundary so the
insert path doesn't race the SPLIT.

The script is the tail of `0004_init_audit_log_metrics.sql`; the
rotation Lambda re-runs that block monthly.
