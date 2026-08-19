#!/bin/bash
set -euo pipefail

# User data for the API gateway EC2 instances.
# Installs .NET 8 runtime, downloads the latest published app from S3,
# configures it as a systemd service, and starts it.

ENVIRONMENT="${environment}"
APP_PORT="${app_port}"
DB_ENDPOINT="${db_endpoint}"
JWT_SECRET_ARN="${jwt_secret_arn}"
DB_CREDS_SECRET_ARN="${db_credentials_secret_arn}"

# Install .NET 8 ASP.NET Core runtime
dnf install -y aspnetcore-runtime-8.0

# Install AWS CLI v2 (already on AL2023 but make sure)
dnf install -y awscli

# Create app user and directories
useradd --system --no-create-home --shell /sbin/nologin apigw || true
mkdir -p /opt/apigw /var/log/apigw /var/lib/apigw/audit-fallback
chown -R apigw:apigw /opt/apigw /var/log/apigw /var/lib/apigw/audit-fallback

# Fetch the latest build artifact from S3 (deployed by CI/CD)
aws s3 cp s3://${project_name}-${environment}-builds/latest.tar.gz /tmp/app.tar.gz \
  --region "${AWS_REGION:-us-east-1}"

tar -xzf /tmp/app.tar.gz -C /opt/apigw
chown -R apigw:apigw /opt/apigw

# Write the systemd service file
cat > /etc/systemd/system/apigw.service <<UNIT
[Unit]
Description=API Gateway (${ENVIRONMENT})
After=network.target

[Service]
User=apigw
Group=apigw
WorkingDirectory=/opt/apigw
ExecStart=/usr/bin/dotnet /opt/apigw/ApiGateway.Api.dll --urls http://0.0.0.0:${APP_PORT}
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=${ENVIRONMENT}
Environment=AWS_REGION=\${AWS_REGION}
Environment=Database__PrimaryConnectionString="Server=${DB_ENDPOINT};Database=ApiGateway;Authentication=SqlPassword;Encrypt=True;TrustServerCertificate=False"
Environment=Secrets__Enabled=true
Environment=Secrets__JwtSigningKeySecretName=${JWT_SECRET_ARN}
Environment=Secrets__DbCredentialsSecretName=${DB_CREDS_SECRET_ARN}
StandardOutput=append:/var/log/apigw/stdout.log
StandardError=append:/var/log/apigw/stderr.log

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable apigw
systemctl start apigw
