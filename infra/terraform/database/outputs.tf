output "primary_endpoint" {
  description = "Primary RDS SQL Server endpoint"
  value       = aws_db_instance.primary.endpoint
}

output "primary_instance_id" {
  description = "RDS instance ID of the primary — used by monitoring module"
  value       = aws_db_instance.primary.id
}

output "read_replica_endpoint" {
  description = "Read replica endpoint (empty string when environment != prod)"
  value       = length(aws_db_instance.read_replica) > 0 ? aws_db_instance.read_replica[0].endpoint : ""
}
