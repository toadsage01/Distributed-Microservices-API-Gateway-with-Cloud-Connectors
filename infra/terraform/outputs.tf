output "vpc_id" {
  description = "ID of the VPC created by the network module"
  value       = module.network.vpc_id
}

output "alb_dns_name" {
  description = "Public DNS name of the Application Load Balancer"
  value       = module.compute.alb_dns_name
}

output "rds_primary_endpoint" {
  description = "RDS SQL Server primary endpoint"
  value       = module.database.primary_endpoint
}

output "rds_read_replica_endpoint" {
  description = "RDS SQL Server read replica endpoint (used by the reporting API)"
  value       = module.database.read_replica_endpoint
}

output "jwt_signing_key_secret_arn" {
  description = "Secrets Manager ARN for the JWT signing key"
  value       = module.secrets.jwt_signing_key_secret_arn
}
