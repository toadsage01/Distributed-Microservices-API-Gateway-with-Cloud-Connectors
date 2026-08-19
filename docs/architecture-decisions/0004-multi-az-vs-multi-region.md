# ADR 0004: Multi-AZ vs multi-region for v1

Date: 2024-12
Status: Accepted

## Context

The gateway brief explicitly excludes multi-region active-active for v1. The remaining
question is which single-region resilience posture to adopt:

1. **Multi-AZ within one region** — primary RDS with a synchronous standby in a second AZ;
   EC2 ASG spread across both AZs.
2. **Single-AZ with frequent backups** — cheaper, but a single AZ outage takes the gateway
   down.

## Decision

Adopt **Multi-AZ within one region** (us-east-1 in the default Terraform config).

- RDS SQL Server in Multi-AZ mode (Always On Availability Groups for Enterprise edition,
  Database Mirroring for Standard edition).
- EC2 ASG spans 2 AZs (us-east-1a, us-east-1b) with `min_size = 2` so a single AZ outage
  always leaves at least one healthy target behind the ALB.
- ALB is internet-facing, deployed across both public subnets.
- Read replica for the reporting path is created only in prod — dev/staging uses the
  primary for both reads and writes.

## Rationale

- **AZ-level failure is common enough to plan for.** AWS's SLA targets 99.99% AZ availability,
  which means an AZ outage is expected ~1 hour/year. A single-AZ deployment would mean an
  hour of downtime per year — unacceptable for a gateway.
- **Multi-AZ failover is fast.** RDS Multi-AZ failover typically completes in 60-120 seconds.
  Combined with ASG spread, the gateway stays reachable from at least one AZ throughout the
  failover.
- **No multi-region complexity.** Multi-region active-active requires cross-region replication,
  conflict resolution, traffic shifting, and a global load balancer. None of that is justified
  for v1.
- **Cost.** Multi-AZ roughly doubles RDS cost but stays in one region. Multi-region would
  roughly quadruple infrastructure cost (two of everything plus cross-region data transfer).

## Trade-offs

- **Region outage takes the gateway offline.** Mitigated by RDS automated snapshots (which
  are cross-region-copyable on a schedule) and the Terraform configuration being
  re-runnable in a fresh region. Full region recovery is a manual ops procedure, not
  automatic.
- **Multi-AZ RDS is more expensive than single-AZ.** Accepted as the cost of avoiding
  per-year downtime.
- **Replication lag between primary and replica.** The reporting API sees writes with a
  small delay. Documented in ADR-0003.

## Alternatives considered

- **Multi-region active-active.** Rejected for v1 per the brief. Tracked as future work —
  would require cross-region saga coordination, which our in-process orchestrator doesn't
  support.
- **Multi-region pilot-light.** A smaller-footprint standby region with RDS cross-region
  read replica and ASG min_size=0, scaled up on failover. Rejected because the failover
  procedure is complex and we'd be paying for the standby without exercising it regularly.

## Follow-ups

- If the gateway's SLA needs to exceed 99.9%, revisit multi-region pilot-light first.
- Cross-region copy of RDS snapshots should be enabled as a baseline DR capability
  regardless of multi-region deployment.
