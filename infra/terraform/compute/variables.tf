variable "project_name" { type = string }
variable "environment" { type = string }
variable "vpc_id" { type = string }
variable "public_subnet_ids" { type = list(string) }
variable "private_subnet_ids" { type = list(string) }
variable "alb_security_group_id" { type = string }
variable "app_security_group_id" { type = string }
variable "db_endpoint" { type = string }
variable "jwt_signing_key_secret_arn" { type = string }
variable "db_credentials_secret_arn" { type = string }
variable "app_port" { type = number }

variable "asg_min_size" {
  description = "Minimum ASG size. 2 ensures Multi-AZ spread"
  type        = number
  default     = 2
}

variable "asg_max_size" {
  description = "Maximum ASG size"
  type        = number
  default     = 6
}

variable "asg_desired_capacity" {
  description = "Desired capacity at boot"
  type        = number
  default     = 2
}

variable "instance_type" {
  description = "EC2 instance type for app servers"
  type        = string
  default     = "t3.large"
}

variable "ami_id" {
  description = "AMI ID for app servers. Defaults to Amazon Linux 2023 x86_64"
  type        = string
  default     = "resolve:ssm:/aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64"
}
