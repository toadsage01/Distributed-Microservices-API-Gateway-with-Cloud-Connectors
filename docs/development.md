# Development Guide

## Prerequisites

- .NET 8 SDK (`dotnet --version` should report 8.x)
- Docker (for Testcontainers integration tests)
- An editor with C# support (VS Code with the C# Dev Kit, Rider, or Visual Studio 2022 17.8+)

## First-time setup

```bash
git clone <repo-url>
cd Distributed-Microservices-API-Gateway-with-Cloud-Connectors
dotnet restore
dotnet build
```

The build should complete cleanly. If you see warnings about XML doc comments, those are
suppressed via `<NoWarn>CS1591</NoWarn>` in `Directory.Build.props` for non-public members.

## Running the API locally

```bash
dotnet run --project src/ApiGateway.Api/ApiGateway.Api.csproj
```

The API listens on `http://localhost:5000`. On startup it runs pending migrations against
the database configured in `appsettings.json` — the dev connection string points at
`localhost` with a placeholder password. Either:

1. Start a local SQL Server instance (Docker is easiest):
   ```bash
   docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=DevPass123!" \
              -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
   ```
   The default `appsettings.json` connection string already matches.

2. Or set the `Database__PrimaryConnectionString` environment variable to point at a
   different instance.

Swagger UI is at `http://localhost:5000/swagger` in Development mode.

## Running the reporting API locally

```bash
dotnet run --project src/ApiGateway.Reporting/ApiGateway.Reporting.csproj
```

The reporting API needs a `Database:ReadReplicaConnectionString` configured — in dev you
can point it at the primary. Migrations are disabled on the reporting host (it never writes
to the DB).

## Running tests

```bash
dotnet test
```

Unit tests run in seconds. Integration tests use Testcontainers to spin up a real SQL Server
2022 container per test session — the container is shared across all integration test classes
via the `SqlServerCollection` fixture. The first integration test run will take longer
because the container image needs to download.

To run only unit tests:

```bash
dotnet test tests/ApiGateway.UnitTests/ApiGateway.UnitTests.csproj
```

## Project structure conventions

- **Domain** layer has zero external dependencies. No `Microsoft.Extensions.*` packages,
  no logging, no DI. Just `System.*` types.
- **Application** layer defines abstractions (`IUpstreamInvoker`, `ISchemaValidator`, etc.)
  and the implementations live in `Application/Infrastructure/`. This keeps the dependency
  arrow pointing inward — the application doesn't reference `ApiGateway.Infrastructure`,
  even though the impl classes do.
- **Infrastructure** layer references `Application` for the abstractions it implements.
- **API** layer wires everything together via `Program.cs`. Layered DI calls:
  `AddApiGatewayInfrastructure` → `AddApiGatewayApplication` → `AddApiGatewayAnomaly`.

## Adding a new migration

1. Pick the next number — `db/migrations/0009_your_description.sql`.
2. Wrap DDL in `IF NOT EXISTS (...) BEGIN ... END GO` so the migration runner can re-execute
   on a partially-applied schema without erroring.
3. For stored procedures and triggers, place them under `db/stored-procedures/` or
   `db/triggers/` and use `CREATE OR ALTER` so the runner applies them on every startup.
4. Update `docs/architecture-decisions/0005-indexing-strategy.md` if your migration adds
   an index — document the access pattern that justifies it.

The migration runner discovers files via EmbeddedResource in `ApiGateway.Infrastructure.csproj`.
Rebuild the project so the new file is embedded before running.

## Adding a new saga step

1. Create a class implementing `ISagaStep` (or `ICompensatableStep` if it has side effects
   that need undoing).
2. Register it in `ApiGateway.Application/ServiceCollectionExtensions.cs`:
   ```csharp
   services.AddScoped<ISagaStep, YourNewStep>();
   ```
3. The orchestrator will pick it up automatically — it injects `IEnumerable<ISagaStep>`
   and orders by `Step.Order` at runtime.

Steps are singletons by default. If your step holds mutable state, scope it per-request
instead — but consider whether the state really belongs on the step vs. the `SagaContext`.

## Adding a new partner

```sql
INSERT INTO PartnerConfig (PartnerId, PartnerName, SchemaVersion, RateLimitTier, AuthMode, IsActive)
VALUES (NEWID(), 'acme', '1.0.0', 'standard', 'jwt', 1);

INSERT INTO SchemaVersion (PartnerId, Version, JsonSchemaBlob, EffectiveDate)
VALUES (
    (SELECT PartnerId FROM PartnerConfig WHERE PartnerName = 'acme'),
    '1.0.0',
    '{"type":"object","required":["name"]}',
    SYSUTCDATETIME()
);
```

## Debugging tips

- **SQL Server container won't start?** Check Docker has enough memory allocated — SQL
  Server needs at least 2GB. The Testcontainers config uses the default `mcr.microsoft.com/mssql/server:2022-latest`
  image.
- **Migration runner says "embedded resource not found"?** Rebuild the Infrastructure project.
  Embedded resources are baked at build time.
- **Swagger UI shows no endpoints?** Make sure the app is running in Development mode
  (`ASPNETCORE_ENVIRONMENT=Development`). The Swagger middleware is gated on
  `app.Environment.IsDevelopment()`.
- **Audit fallback queue piling up?** The DB is unreachable. Check the primary connection
  string and the SQL Server container. The `AuditFallbackRecoveryService` logs at Information
  when it drains entries.
