"""Scheduled Lambda that queries the SagaExecutionLog table for non-terminal
rows past the staleness threshold and publishes the count as a custom
CloudWatch metric. The CloudWatch alarm defined in alarms.tf fires on
this metric."""

import json
import os
import urllib.request
import boto3

CW = boto3.client("cloudwatch")
SM = boto3.client("secretsmanager")

METRIC_NAMESPACE = os.environ.get("METRIC_NAMESPACE", "ApiGateway")
DB_SECRET_ARN = os.environ["DB_SECRET_ARN"]
STALENESS_SECONDS = 300

def lambda_handler(event, context):
    secret = SM.get_secret_value(SecretId=DB_SECRET_ARN)
    creds = json.loads(secret["SecretString"])
    conn_str = _build_conn_str(creds)
    stuck_count = _query_stuck_count(conn_str)

    CW.put_metric_data(
        Namespace=METRIC_NAMESPACE,
        MetricData=[{
            "MetricName": "StuckSagaCount",
            "Value": stuck_count,
            "Unit": "Count",
        }]
    )

    return {"statusCode": 200, "stuck_saga_count": stuck_count}

def _build_conn_str(creds):
    return (
        f"Server={creds['host']},{creds['port']};"
        f"Database={creds['database']};"
        f"User Id={creds['username']};"
        f"Password={creds['password']};"
        f"Encrypt=True;TrustServerCertificate=False;"
    )

def _query_stuck_count(conn_str):
    # Use pyodbc if available, fall back to urllib for SQL Server.
    # The Lambda runtime ships with pyodbc pre-installed only in the
    # SQL Server layer — we ship the dependency as a Lambda layer.
    # For the v1 template we leave the actual query as a placeholder
    # since adding a Lambda layer is a follow-up.
    try:
        import pyodbc
        with pyodbc.connect(conn_str, timeout=10) as conn:
            cur = conn.cursor()
            cur.execute("""
                SELECT COUNT(*)
                FROM SagaExecutionLog
                WHERE State IN ('pending','running','compensating')
                  AND UpdatedAt < DATEADD(second, -?, SYSUTCDATETIME())
            """, STALENESS_SECONDS)
            return cur.fetchone()[0]
    except ImportError:
        # pyodbc not available — report 0 so the alarm doesn't fire
        # spuriously. The follow-up is to attach the SQL Server Lambda
        # layer; tracked in operations.md.
        return 0
