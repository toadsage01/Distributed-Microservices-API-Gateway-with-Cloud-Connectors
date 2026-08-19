variable "project_name" {
  type = string
}

variable "environment" {
  type = string
}

variable "vpc_cidr" {
  description = "CIDR for the VPC. /16 supports the multi-AZ layout below"
  type        = string
  default     = "10.20.0.0/16"
}

variable "availability_zones" {
  description = "AZs to spread across. At least 2 required for Multi-AZ."
  type        = list(string)
  default     = ["us-east-1a", "us-east-1b"]
}
