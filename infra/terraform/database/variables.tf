variable "project_name" { type = string }
variable "environment" { type = string }
variable "vpc_id" { type = string }
variable "private_subnet_ids" { type = list(string) }
variable "db_security_group_id" { type = string }
variable "sql_server_admin_password_secret_arn" { type = string }

variable "sql_server_engine_version" {
  description = "SQL Server engine version. Multi-AZ requires Enterprise or Web edition for some versions; default 15.00 = SQL Server 2019"
  type        = string
  default     = "15.00"
}

variable "sql_server_edition" {
  description = "SQL Server edition — Enterprise supports Multi-AZ Always On; Standard uses DBM"
  type        = string
  default     = "enterprise"

  validation {
    condition     = contains(["enterprise", "standard", "web", "express"], var.sql_server_edition)
    error_message = "sql_server_edition must be one of enterprise/standard/web/express."
  }
}

variable "rds_instance_class" {
  description = "RDS instance class"
  type        = string
  default     = "db.r6g.large"
}

variable "storage_gb" {
  description = "Allocated storage in GB"
  type        = number
  default     = 200
}

variable "storage_type" {
  description = "Storage type — gp3 is the modern default"
  type        = string
  default     = "gp3"
}

variable "backup_retention_days" {
  description = "Automated backup retention"
  type        = number
  default     = 14
}
