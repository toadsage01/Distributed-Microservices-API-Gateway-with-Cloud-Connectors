# ADR 0001: Saga orchestration vs choreography

Date: 2024-12
Status: Accepted

## Context

The gateway mediates requests that span multiple upstream calls (transform → call upstream A →
call upstream B → aggregate). A mid-sequence failure must be compensatable — if upstream B
failed, we need to undo whatever side effect upstream A committed.

Two well-known patterns apply:

1. **Choreography** — each step emits an event; downstream steps react. No central coordinator.
2. **Orchestration** — a coordinator drives the workflow step by step and decides on compensation.

## Decision

We use **orchestration** with an in-process `SagaOrchestrator`. The orchestrator lives in the
gateway process — there is no separate coordinator service — and persists state to
`SagaExecutionLog` so a process crash leaves a recoverable trail.

## Rationale

- **Recovery determinism.** With orchestration, recovery is a single function: read the
  `SagaExecutionLog`, walk it in reverse, invoke the compensatable handlers. Choreography would
  require stitching together event logs from multiple consumers to reconstruct saga state —
  painful at 3am during an incident.
- **Operational simplicity.** The gateway brief explicitly favours a "lightweight" footprint.
  A separate coordinator service is another deployment, another scaling concern, another
  observability surface. In-process orchestration keeps the blast radius small.
- **Multi-step visibility.** A saga that spans three upstreams needs to expose step state for
  operator triage. With a `SagaExecutionLog` table this is a SQL query; with choreography it
  would require correlating events across consumer logs.
- **Compensation ordering.** Orchestration makes the reverse-order compensation walk trivial
  (`ORDER BY StepOrder DESC`). Choreography requires each consumer to track the order in which
  it acted, which is implicit and easy to get wrong.

## Trade-offs

- **Orchestrator is a single point of failure.** Mitigated by `SagaExecutionLog` state — on
  process restart the `SagaRecoveryService` picks up where we left off.
- **Coupling.** Step implementations are registered in the orchestrator's DI container. We
  accept this because the steps share the `SagaContext` and would be coupled anyway via the
  context object.
- **Doesn't scale to cross-service sagas.** If a future saga needs to span services outside the
  gateway, we'd need to revisit. For v1 all saga steps live in-process.

## Alternatives considered

- **Choreography with an event bus** (e.g., SNS+SQS, EventBridge) — rejected for v1 because
  the operational complexity of reconstructing saga state across consumers doesn't pay off
  until we have more independent services participating in each saga.
- **External orchestrator** (e.g., AWS Step Functions, Temporal) — rejected for v1 because
  we don't need cross-service sagas yet, and adding another AWS service to the deployment
  footprint for the gateway alone is over-engineering.

## Follow-ups

- If we add a service that needs to participate in a saga, revisit by extracting the
  orchestrator into its own service and using an event bus for inter-service communication.
  This is not v1 work.
