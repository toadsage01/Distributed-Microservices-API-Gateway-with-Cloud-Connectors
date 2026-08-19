output "jwt_signing_key_secret_arn" {
  value = aws_secretsmanager_secret.jwt_signing_key.arn
}

output "db_password_secret_arn" {
  value = aws_secretsmanager_secret.db_password.arn
}

output "db_credentials_secret_arn" {
  description = "Composite secret ARN used by the app's SecretsManagerClient"
  value       = aws_secretsmanager_secret.db_credentials_composite.arn
}
