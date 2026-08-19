variable "aws_region" {
  description = "AWS region for all resources"
  type        = string
  default     = "us-east-1"
}

variable "environment" {
  description = "Environment name (dev / staging / prod). Used for tagging and naming"
  type        = string
  default     = "prod"

  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "environment must be one of dev, staging, prod."
  }
}

variable "project_name" {
  description = "Short project name used as a prefix for all resource names"
  type        = string
  default     = "apigw"
}

variable "sql_server_admin_password" {
  description = "Master password for the RDS SQL Server instance. Marked sensitive."
  type        = string
  sensitive   = true
}

variable "jwt_signing_key" {
  description = "JWT signing key for the gateway. Marked sensitive."
  type        = string
  sensitive   = true
}

variable "app_port" {
  description = "Port the gateway ASP.NET Core app listens on"
  type        = number
  default     = 8080
}
