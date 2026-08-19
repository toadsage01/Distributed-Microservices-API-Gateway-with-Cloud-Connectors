locals {
  name_prefix = "${var.project_name}-${var.environment}"
}

# JWT signing key — plaintext secret so the .NET runtime can fetch
# the raw key material via GetSecretValue without a JSON parse step.
resource "aws_secretsmanager_secret" "jwt_signing_key" {
  name        = "${local.name_prefix}/jwt-signing-key"
  description = "JWT HMAC signing key for the API gateway"

  recovery_window_in_days = var.environment == "prod" ? 30 : 7

  tags = {
    Name        = "${local.name_prefix}-jwt-signing-key"
    Environment = var.environment
  }
}

resource "aws_secretsmanager_secret_version" "jwt_signing_key" {
  secret_id     = aws_secretsmanager_secret.jwt_signing_key.id
  secret_string = var.jwt_signing_key
}

# DB master password — separate secret so DB rotation can be wired up
# later without touching the JWT key.
resource "aws_secretsmanager_secret" "db_password" {
  name        = "${local.name_prefix}/db-credentials"
  description = "SQL Server master credentials for the API gateway RDS"

  recovery_window_in_days = var.environment == "prod" ? 30 : 7

  tags = {
    Name        = "${local.name_prefix}-db-credentials"
    Environment = var.environment
  }
}

resource "aws_secretsmanager_secret_version" "db_password" {
  secret_id     = aws_secretsmanager_secret.db_password.id
  secret_string = var.sql_server_admin_password
}

# Bundle the JWT + DB credentials into a single "apigateway/db-credentials"
# secret with a JSON body that the SecretsManagerClient in the app can
# deserialise straight into a typed options object. This avoids the app
# having to make two secrets calls on startup.
resource "aws_secretsmanager_secret" "db_credentials_composite" {
  name        = "${local.name_prefix}/db-credentials-composite"
  description = "Composite DB credentials for the app startup path"

  recovery_window_in_days = var.environment == "prod" ? 30 : 7

  tags = {
    Name        = "${local.name_prefix}-db-credentials-composite"
    Environment = var.environment
  }
}

resource "aws_secretsmanager_secret_version" "db_credentials_composite" {
  secret_id = aws_secretsmanager_secret.db_credentials_composite.id
  secret_string = jsonencode({
    host     = "" # filled in at deploy time via a null_resource or external script
    port     = 1433
    username = "admin"
    password = var.sql_server_admin_password
    database = "ApiGateway"
  })
}
