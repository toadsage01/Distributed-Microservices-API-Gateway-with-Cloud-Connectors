# Compliance Baseline

## Applicable standard

No specific regulatory driver was designated for this build. The default baseline is
**SOC 2-aligned** — broadly applicable to a generic API gateway without over- or
under-building for a domain that wasn't specified.

If the actual deployment target later turns out to require PCI DSS (e.g., gateway routes
payment traffic) or HIPAA (e.g., gateway routes PHI), this baseline will need a follow-up
hardening pass. The current config does **not** claim compliance with any standard that
wasn't verified against.

## Controls implemented

### Encryption

| Control | Implementation | Verified by |
|---|---|---|
| Encryption at rest — RDS storage | `storage_encrypted = true` on `aws_db_instance` | Terraform plan shows the attribute |
| Encryption at rest — Secrets Manager | Default ( Secrets Manager encrypts all secrets) | AWS service default |
| Encryption in transit — ALB | TLS 1.3 listener policy `ELBSecurityPolicy-TLS13-1-2-2021-06` | Terraform config |
| Encryption in transit — RDS | `Encrypt=True;TrustServerCertificate=False` in production connection string | `appsettings.Production.json` + Secrets Manager composite secret |

### Access control

| Control | Implementation |
|---|---|
| Least-privilege IAM | EC2 instance role scoped to specific Secrets Manager ARNs and CloudWatch PutMetricData only — see `compute/main.tf` |
| Security group scoping | ALB → app on app_port only; app → RDS on 1433 only — see `network/main.tf` |
| No long-lived credentials in code | JWT signing key + DB credentials resolved from Secrets Manager at runtime |
| Branch protection | Documented in README — main branch should require PR review and status checks (manual follow-up if PAT lacks admin scope) |

### Audit trail

| Control | Implementation |
|---|---|
| Security event logging | `AuditLog_Security` — sync path, durable before response returns |
| High-volume telemetry | `AuditLog_Metrics` — async path via Channel<T> + SqlBulkCopy |
| Anomaly detection | `SecurityAlert` table populated by trigger (fast-path) + statistical scorer (out-of-band) |
| Retention — security events | Indefinite — `AuditLog_Security` is never deleted |
| Retention — telemetry | 90 days hot, then archived to S3 via partition swap |
| Tamper-evidence | Append-only — no UPDATE statements issued against `AuditLog_Security` or `AuditLog_Metrics` by application code; triggers disabled in dev |

### Availability

| Control | Implementation |
|---|---|
| Multi-AZ | RDS Multi-AZ in prod; EC2 ASG spans 2 AZs with `min_size = 2` |
| Automated backups | RDS backup retention 14 days, point-in-time recovery within window |
| Health probes | `/health/live` (process up) + `/health/ready` (DB reachable); ALB probes readiness |
| Deployment safety | ASG instance refresh with `MinHealthyPercentage=50`; manual approval gate for prod deploys |

### Monitoring

| Control | Implementation |
|---|---|
| Latency | CloudWatch alarm on ALB TargetResponseTime p99 |
| Error rate | CloudWatch alarm on ALB 5xx count |
| Capacity | CloudWatch alarm on ASG unhealthy host count + RDS CPU + RDS free storage |
| Workflow integrity | Custom CloudWatch metric `StuckSagaCount` from scheduled Lambda |
| Anomaly detection | Statistical scorer (z-score) + trigger-based burst detection |

## What's NOT covered

If you need to claim compliance with a specific standard, the following gaps must be
addressed first:

### For PCI DSS

- **Network segmentation** between cardholder data environment and other systems. The
  current Terraform has a single VPC; PCI would require isolated VPCs or subnets with
  strict firewall rules.
- **Quarterly ASV scans** of the ALB.
- **WAF in front of the ALB** (AWS WAF or equivalent). Not provisioned in v1 Terraform.
- **Key management** via FIPS 140-2 Level 3 HSM. AWS KMS satisfies Level 2; CloudHSM for
  Level 3.
- **File Integrity Monitoring (FIM)** on EC2 instances.

### For HIPAA

- **BAA (Business Associate Agreement)** with AWS for any service that touches PHI.
- **Encryption of backup snapshots** with customer-managed KMS keys (currently uses
  AWS-managed default key).
- **PHI access logging** beyond what `AuditLog_Security` captures — need to log every
  read of PHI specifically, not just security events.
- **Data residency** — ensure the chosen AWS region and any cross-region backup copies
  remain within approved jurisdictions.

## Audit checklist

When preparing for an external audit:

1. Run `terraform plan` against production and confirm the attributes above are present.
2. Pull a sample of `AuditLog_Security` rows and verify the event types and detail
   fields are populated correctly.
3. Confirm the `StuckSagaCount` Lambda is running and publishing metrics (check
   CloudWatch metric `ApiGateway/StuckSagaCount`).
4. Verify the IAM policy attached to the EC2 instance role is scoped only to the specific
   Secrets Manager ARNs (no wildcards on `secretsmanager:GetSecretValue`).
5. Confirm `AuditLog_Metrics` partition rotation ran within the last month (check the
   partition function's range values).
