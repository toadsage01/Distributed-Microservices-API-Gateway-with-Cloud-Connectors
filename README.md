# Distributed Microservices API Gateway

A production-grade API gateway built on .NET 8 that mediates traffic between external partners
and internal enterprise services. The gateway handles idempotency under retry, multi-partner
schema drift, audit-log write amplification, multi-step workflow recovery, and out-of-band
anomaly detection.

## Architecture at a Glance

```
Client → [JWT Auth] → [Rate Limiter] → [Schema Validator: partner + version aware]
  → Saga Orchestrator (correlation_id assigned; SagaExecutionLog: pending)
      → Transform → Call Upstream A → Call Upstream B → Aggregate
      → On failure: replay SagaExecutionLog backward, invoke ICompensatableStep handlers
  → Response to client
  ⇢ [async, non-blocking] Sync audit writer → AuditLog_Security
  ⇢ [async, non-blocking] Channel<T> → BackgroundService batch → AuditLog_Metrics
  ⇢ Anomaly detector subscribes to the metrics stream → SecurityAlert on deviation
```

The architecture is **Clean / Onion**:

| Layer | Project | Role |
|---|---|---|
| Domain | `ApiGateway.Domain` | Entities, value objects, state machines — no external deps |
| Application | `ApiGateway.Application` | Sagas, CQRS command/query handlers, contracts, abstractions |
| Infrastructure | `ApiGateway.Infrastructure` | ADO.NET repositories, sync+async audit writers, Secrets Manager, migration runner |
| Infrastructure.Anomaly | `ApiGateway.Infrastructure.Anomaly` | Statistical anomaly scorer + detection background service |
| API | `ApiGateway.Api` | Middleware pipeline (JWT, rate limit, schema validation), gateway controller, OpenAPI |
| Reporting | `ApiGateway.Reporting` | Read-only CQRS query API on the read replica |

Dependencies point inward only — Infrastructure references Application and Domain; Application
references Domain; Domain references nothing.

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

### Prerequisites

- .NET 8 SDK
- Docker (for Testcontainers integration tests)
- SQL Server 2022+ (or use the Docker compose dev setup)
- AWS CLI configured with appropriate credentials (only for production deploys)

### Local development

```bash
dotnet restore
dotnet build
dotnet test
```

Integration tests use Testcontainers to provision a real SQL Server 2022 container per test
session — no local database setup required. The container is started automatically and torn down
at the end of the session.

### Running the API locally

```bash
dotnet run --project src/ApiGateway.Api
```

The API listens on `http://localhost:5000`. Swagger UI is available at
`http://localhost:5000/swagger`.

### Running migrations against a local database

```bash
for f in db/migrations/*.sql db/stored-procedures/*.sql db/triggers/*.sql; do
    sqlcmd -S localhost -d ApiGateway -U sa -P "$SA_PASSWORD" -i "$f"
done
```

Or programmatically:

```csharp
await host.Services.RunMigrationsAsync();
```

## Architecture Decisions

| ADR | Title | Status |
|---|---|---|
| [0001](docs/architecture-decisions/0001-saga-vs-choreography.md) | Saga orchestration vs choreography | Accepted |
| [0002](docs/architecture-decisions/0002-hybrid-audit-split.md) | Hybrid sync/async audit logging split | Accepted |
| [0003](docs/architecture-decisions/0003-cqrs-lite-scope.md) | CQRS-lite scope boundary (no event sourcing) | Accepted |
| [0004](docs/architecture-decisions/0004-multi-az-vs-multi-region.md) | Multi-AZ vs multi-region for v1 | Accepted |
| [0005](docs/architecture-decisions/0005-indexing-strategy.md) | Per-table indexing strategy and rationale | Accepted |
| [0006](docs/architecture-decisions/0006-resilience-policies.md) | Polly resilience policies on outbound HTTP | Accepted |

## Infrastructure

The deployment target is AWS, provisioned via Terraform. Five composable modules wire together
under a root `main.tf`:

| Module | Purpose |
|---|---|
| `network/` | VPC, public/private subnets across 2 AZs, security groups |
| `compute/` | ALB + ASG + launch template, IAM instance role scoped least-privilege |
| `database/` | RDS SQL Server Multi-AZ (Enterprise edition) + read replica |
| `secrets/` | Secrets Manager entries for JWT signing key + DB credentials |
| `monitoring/` | CloudWatch alarms + scheduled Lambda for stuck-saga detection |

```bash
cd infra/terraform
terraform init
terraform plan -var="jwt_signing_key=$(openssl rand -hex 32)" \
               -var="sql_server_admin_password=$(openssl rand -base64 32)"
terraform apply
```

See [docs/architecture.md](docs/architecture.md) for the full system overview,
[docs/development.md](docs/development.md) for the dev setup walkthrough, and
[docs/operations.md](docs/operations.md) for runbook procedures.

## CI/CD

| Workflow | Purpose |
|---|---|
| `.github/workflows/ci.yml` | Restore → format verify → build → unit tests → integration tests → publish OpenAPI spec |
| `.github/workflows/cd-deploy.yml` | Build → terraform plan + manual approval gate → terraform apply → ASG rolling deploy |

CI runs against every push and pull request. CD triggers on merge to `main` and requires
manual approval for production deploys. The ASG instance refresh strategy (`Rolling` with
`MinHealthyPercentage=50`) ensures zero-downtime deploys and automatic rollback if the new
instances fail their health checks.

Branch protection on `main` should require:
- Status checks: `Build, format, test` must pass
- Require pull request reviews before merging (at least 1 approval)
- Require branches to be up to date before merging

The PAT used to configure the repo may not have admin scope to set branch protection
programmatically — this is a manual follow-up if the PAT lacks that scope.

## Compliance Baseline

Default baseline is **SOC 2-aligned** (broadly applicable, doesn't over- or under-build for a
domain that wasn't specified):

- **Encryption at rest**: RDS storage encryption, Secrets Manager encryption
- **Encryption in transit**: TLS 1.2+ everywhere (ALB listener enforces TLS 1.3 policy in
  production), no plaintext internal traffic between ALB and EC2 instances
- **Least-privilege IAM**: EC2 instance role scoped to specific Secrets Manager ARNs and
  CloudWatch actions only
- **Audit retention**: `AuditLog_Security` retained indefinitely (partitioned/archived);
  `AuditLog_Metrics` retained 90 days hot, then archived to S3 via the monthly partition swap

If the actual deployment target later requires PCI or HIPAA, this baseline will need a
follow-up hardening pass — the current config does not claim compliance with a standard that
wasn't verified against. See [docs/compliance.md](docs/compliance.md) for details.

## Known Limitations

- **Single-region Multi-AZ only.** Multi-region active-active is out of scope for v1 and
  tracked in ADR-0004. A region outage will take the gateway offline until manual failover.
- **In-process rate limiter.** Per-instance sliding window, not Redis-backed. The ALB-level
  burst protection catches volumetric attacks before they reach the app.
- **In-memory anomaly baseline.** On cold start the scorer refuses to score until
  `MinSampleCount` samples accumulate — typically under a minute of normal traffic. Baseline
  persistence is deferred.
- **No GraphQL.** v1 is REST-only.
- **Saga recovery service is intentionally conservative.** It only re-runs compensation for
  sagas whose state is `failed`. `pending`/`running` steps are surfaced as alerts but require
  manual review because the gateway cannot determine upstream state without consulting the
  upstream system.
- **Reporting API shares the application layer's query handlers.** This means query changes
  affect both hosts — a future split would extract a separate Reporting.Application project.

## License

Internal — not for redistribution.
