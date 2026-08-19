# Operations Runbook

## Health probes

| Endpoint | Purpose | Healthy Response |
|---|---|---|
| `GET /health/live` | Liveness probe — process is up | 200 + `{"status":"healthy"}` |
| `GET /health/ready` | Readiness probe — DB is reachable | 200 + `{"status":"ready","database":"reachable"}` |

The ALB readiness check hits `/health/ready` every 15s. If the DB is unreachable, the
probe returns 503 and the ALB pulls the target out of rotation without killing the process.

## CloudWatch alarms

The Terraform `monitoring/` module provisions:

| Alarm | Threshold | Action |
|---|---|---|
| `*-alb-5xx-rate` | >10 5xx responses per minute for 3 minutes | Investigate upstream failures |
| `*-target-response-p99` | Maximum p99 >2000ms for 3 minutes | Investigate slow upstream or DB contention |
| `*-unhealthy-hosts` | Any unhealthy host for 2 minutes | Check EC2 instance logs, ASG activity |
| `*-rds-cpu-high` | RDS CPU >85% for 5 minutes | Consider scaling up RDS instance class |
| `*-rds-low-storage` | Free storage <10GB | Increase allocated storage |
| `*-saga-stuck` | StuckSagaCount >0 | Investigate stuck sagas via `SagaExecutionLog` |

The `StuckSagaCount` alarm is fed by a scheduled Lambda that runs every 5 minutes and
publishes the count of `SagaExecutionLog` rows in non-terminal state past the 5-minute
staleness threshold.

## Common incidents

### Saga stuck in `compensating` state

**Symptom**: `StuckSagaCount` alarm fires; reporting API shows a saga whose steps are in
`compensating` state for >5 minutes.

**Investigation**:

1. Query for the stuck saga:
   ```sql
   SELECT Id, CorrelationId, StepName, State, UpdatedAt
   FROM SagaExecutionLog
   WHERE State = 'compensating'
     AND UpdatedAt < DATEADD(minute, -5, SYSUTCDATETIME());
   ```

2. Check application logs for the correlation id — the `SagaOrchestrator` logs at Error
   when compensation fails:
   ```
   "Compensation failed for step {Step}, correlation {Corr} — manual intervention required"
   ```

3. Determine whether the upstream compensation endpoint is reachable:
   - Check the upstream's health endpoint
   - Check CloudWatch metrics for the upstream's latency / 5xx rate

4. If the upstream is down, the saga will remain stuck until the upstream recovers. The
   `SagaRecoveryService` will retry compensation every minute automatically — no manual
   action needed beyond confirming the upstream is being restored.

5. If the upstream is up but compensation is still failing, manually inspect the
   `PayloadSnapshot` to understand what the compensation call is trying to do:
   ```sql
   SELECT StepName, PayloadSnapshot
   FROM SagaExecutionLog
   WHERE CorrelationId = '<corr-id>'
     AND State = 'compensating';
   ```

6. If manual intervention is required, update the step state to `failed` after the upstream
   side effect has been reconciled out-of-band:
   ```sql
   UPDATE SagaExecutionLog
   SET State = 'failed', UpdatedAt = SYSUTCDATETIME()
   WHERE Id = '<step-id>';
   ```
   This will stop the recovery service from retrying.

### Auth failure burst

**Symptom**: `SecurityAlert` table has rows with `PatternType = 'auth_failure_burst'` for
a partner; the trigger fired because AuditLog_Security had >10 AUTH_FAILURE rows in 60s.

**Investigation**:

1. Check the alert detail:
   ```sql
   SELECT TOP 50 * FROM SecurityAlert
   WHERE PatternType = 'auth_failure_burst'
   ORDER BY RaisedAt DESC;
   ```

2. Look at the underlying AUTH_FAILURE events:
   ```sql
   SELECT EventType, Detail, OccurredAt
   FROM AuditLog_Security
   WHERE PartnerId = '<partner-id>'
     AND EventType = 'AUTH_FAILURE'
     AND OccurredAt > DATEADD(minute, -15, SYSUTCDATETIME())
   ORDER BY OccurredAt DESC;
   ```

3. Determine the cause:
   - **Credential stuffing**: rotate the partner's API credentials, alert the partner.
   - **Key rotation in progress**: partner's new key may not be propagated yet; check
     JWT signing key cache TTL (10min) and force a refresh if needed.
   - **Bug in partner client**: partner may be sending expired tokens; contact partner.

4. If the burst is ongoing and impacting service, temporarily deactivate the partner:
   ```sql
   UPDATE PartnerConfig SET IsActive = 0 WHERE PartnerId = '<partner-id>';
   ```
   In-flight sagas will continue, but new requests will be rejected by the
   `JwtAuthMiddleware` (since `PartnerConfigLoader` returns null for inactive partners).

### Database unavailability

**Symptom**: Sync audit writer logs "Primary DB unavailable for audit write — falling back
to local queue". Eventually `AuditFallbackRecoveryService` should drain the queue when the
DB reconnects.

**Investigation**:

1. Check RDS instance status via the AWS console or CLI:
   ```bash
   aws rds describe-db-instances --db-instance-identifier <name>
   ```

2. If RDS is failing over (Multi-AZ), wait for the failover to complete (typically 60-120s).
   The application will reconnect automatically.

3. Check the fallback queue depth:
   ```bash
   ls /var/lib/apigw/audit-fallback/ | wc -l
   ```
   The recovery service drains up to 500 entries per cycle (every 30s). If the queue is
   growing faster than the drain rate, the DB has been down for an extended period.

4. Once the DB is reachable, monitor the queue depth — it should drain to zero within
   `queue_depth / 500 * 30s` seconds.

### Deploy failed mid-ASG-refresh

**Symptom**: ASG instance refresh failed; some instances are on the new version, some on the
old.

**Action**:

1. Check the ASG instance refresh status:
   ```bash
   aws autoscaling describe-instance-refreshes \
     --auto-scaling-group-name <asg-name> \
     --query 'max_by(InstanceRefreshes, &StartTime)'
   ```

2. If the refresh status is `Failed`, the ASG will stop replacing instances. Old instances
   continue serving traffic; new instances may be marked unhealthy and replaced.

3. To roll back, trigger a new instance refresh with the previous build artifact:
   ```bash
   aws s3 cp s3://<bucket>/previous.tar.gz latest.tar.gz
   aws s3 cp latest.tar.gz s3://<bucket>/latest.tar.gz
   aws autoscaling start-instance-refresh --auto-scaling-group-name <asg-name> \
     --strategy Rolling --preferences MinHealthyPercentage=50,InstanceWarmup=60
   ```

4. To cancel a refresh in progress:
   ```bash
   aws autoscaling cancel-instance-refresh --auto-scaling-group-name <asg-name>
   ```

## Backup and restore

- **RDS automated backups**: 14-day retention (configurable via Terraform). Point-in-time
  recovery is available within the backup retention window.
- **RDS snapshots**: Manual snapshots can be taken before risky migrations. The Terraform
  config sets `deletion_protection = true` in prod, so the RDS instance cannot be deleted
  without explicit unprotection.
- **Cross-region snapshot copy**: Should be configured as a baseline DR capability. This
  is not yet automated in the Terraform config — see ADR-0004 follow-ups.

## Secrets rotation

- **JWT signing key**: Rotate by updating the Secrets Manager secret. The application's
  SecretsManagerClient cache TTL is 10 minutes, so a rotation propagates within 10 minutes
  without a restart.
- **RDS master password**: Rotation requires an RDS modification (apply immediately or
  during the next maintenance window). Update both the Secrets Manager secret and the RDS
  instance password in the same operation.

## Audit log retention

- `AuditLog_Security`: Retained indefinitely. The table is not partitioned — size grows
  linearly with security event volume (typically low).
- `AuditLog_Metrics`: Partitioned monthly. Partitions older than 90 days are swapped out
  to S3 via the partition-rotation Lambda (monthly schedule). The S3 lifecycle policy
  should move them to Glacier after 1 year and delete after 7 years (configurable).
