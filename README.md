# Distributed Microservices API Gateway

A production-grade API gateway built on .NET 8 that mediates traffic between external partners
and internal enterprise services. The gateway handles idempotency under retry, multi-partner
schema drift, audit-log write amplification, multi-step workflow recovery, and out-of-band
anomaly detection.

The full architecture rationale, setup steps, and known limitations live in [docs/](docs/).
See [docs/architecture.md](docs/architecture.md) for the system overview and
[docs/architecture-decisions/](docs/architecture-decisions/) for the ADRs that drove the
non-obvious decisions.

## Repository Layout

```
src/                Solution source code (Clean / Onion Architecture)
  ApiGateway.Domain/                  Entities, value objects, no external deps
  ApiGateway.Application/             Sagas, CQRS command/query handlers, contracts
  ApiGateway.Infrastructure/         ADO.NET repositories, audit writers, secrets
  ApiGateway.Infrastructure.Anomaly/  Statistical anomaly scoring
  ApiGateway.Api/                     Inbound HTTP host, middleware pipeline, OpenAPI
  ApiGateway.Reporting/               Read-only CQRS query API (read replica)
tests/               xUnit + Testcontainers integration suites
db/                  Forward-only SQL migrations, stored procs, triggers
infra/terraform/     Composable AWS infrastructure modules
.github/workflows/   CI build + CD deployment pipelines
docs/                ADRs, OpenAPI spec, architecture notes
```

## Quickstart

The solution targets .NET 8 and SQL Server. See [docs/development.md](docs/development.md)
for the full local setup; the short version:

```bash
dotnet restore
dotnet build
dotnet test
```

CI uses Testcontainers to provision a real SQL Server instance, so the integration suites
do not require any local database setup.

## Status

This is the v1 surface called out in the architecture brief — single-region Multi-AZ,
CQRS-lite, orchestrated in-process Sagas. Multi-region active-active and GraphQL are
explicitly out of scope for v1 and tracked as future work in the ADRs.
