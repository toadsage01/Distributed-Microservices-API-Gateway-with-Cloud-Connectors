locals {
  name_prefix = "${var.project_name}-${var.environment}"

  # Multi-AZ: Always On AG for Enterprise edition, DBM for Standard.
  # The Terraform AWS provider selects the mode automatically based on
  # the engine edition — we just set multi_az = true.
  multi_az = var.environment == "prod" ? true : false
}

# Subnet group spanning all private subnets so Multi-AZ failover has a
# subnet in each AZ to land in.
resource "aws_db_subnet_group" "main" {
  name        = "${local.name_prefix}-db-subnet-group"
  subnet_ids  = var.private_subnet_ids
  description = "Subnet group for ${local.name_prefix} RDS"

  tags = {
    Name = "${local.name_prefix}-db-subnet-group"
  }
}

# Resolve the master password from Secrets Manager rather than passing
# it through Terraform state. The secret must be a plaintext secret
# (not a key/value) — the secrets module formats it that way.
data "aws_secretsmanager_secret_version" "db_password" {
  secret_id = var.sql_server_admin_password_secret_arn
}

# Primary RDS instance — Multi-AZ SQL Server. Encryption at rest is on
# by default for SQL Server on RDS via the KMS key derived from the
# account default unless overridden.
resource "aws_db_instance" "primary" {
  identifier                 = "${local.name_prefix}-primary"
  engine                     = "sqlserver-${var.sql_server_edition == "express" ? "ex" : var.sql_server_edition == "standard" ? "se" : var.sql_server_edition == "enterprise" ? "ee" : "web"}"
  engine_version             = var.sql_server_engine_version
  instance_class             = var.rds_instance_class
  allocated_storage          = var.storage_gb
  storage_type                = var.storage_type
  storage_encrypted           = true

  multi_az                    = local.multi_az

  db_name                     = "ApiGateway"
  username                    = "admin"
  password                    = data.aws_secretsmanager_secret_version.db_password.secret_string
  port                        = 1433

  vpc_security_group_ids      = [var.db_security_group_id]
  db_subnet_group_name        = aws_db_subnet_group.main.name

  backup_retention_period     = var.backup_retention_days
  backup_window               = "03:00-04:00"
  maintenance_window          = "sun:04:00-sun:06:00"

  auto_minor_version_upgrade  = true
  deletion_protection         = var.environment == "prod"

  copy_tags_to_snapshot       = true

  tags = {
    Name        = "${local.name_prefix}-primary"
    Environment = var.environment
  }
}

# Read replica for the CQRS reporting path. Only created in prod —
# dev/staging hits the primary for both reads and writes.
resource "aws_db_instance" "read_replica" {
  count                       = var.environment == "prod" ? 1 : 0
  identifier                  = "${local.name_prefix}-read-replica"
  replicate_source_db         = aws_db_instance.primary.identifier
  instance_class              = var.rds_instance_class
  allocated_storage           = var.storage_gb
  storage_type                = var.storage_type
  storage_encrypted           = true

  vpc_security_group_ids      = [var.db_security_group_id]
  db_subnet_group_name        = aws_db_subnet_group.main.name

  auto_minor_version_upgrade  = true
  deletion_protection         = var.environment == "prod"

  tags = {
    Name        = "${local.name_prefix}-read-replica"
    Environment = var.environment
    Role        = "read-replica"
  }
}
