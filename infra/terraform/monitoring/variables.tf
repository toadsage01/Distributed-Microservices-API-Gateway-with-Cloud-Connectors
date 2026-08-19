variable "project_name" { type = string }
variable "environment" { type = string }
variable "alb_arn" { type = string }
variable "asg_name" { type = string }
variable "db_instance_id" { type = string }

variable "p99_threshold_ms" {
  description = "CloudWatch alarm threshold for p99 latency"
  type        = number
  default     = 2000
}

variable "error_rate_threshold" {
  description = "ALB 5xx rate as a fraction (0.05 = 5%)"
  type        = number
  default     = 0.05
}

variable "saga_stale_threshold" {
  description = "Number of SagaExecutionLog rows stuck in non-terminal state past threshold"
  type        = number
  default     = 0
}
