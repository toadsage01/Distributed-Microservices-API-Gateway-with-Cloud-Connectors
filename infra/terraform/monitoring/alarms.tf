locals {
  name_prefix = "${var.project_name}-${var.environment}"
}

# CloudWatch alarm: ALB 5xx rate
resource "aws_cloudwatch_metric_alarm" "alb_5xx_rate" {
  alarm_name          = "${local.name_prefix}-alb-5xx-rate"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  metric_name         = "HTTPCode_ELB_5XX_Count"
  namespace           = "AWS/ApplicationELB"
  period              = 60
  statistic           = "Sum"
  threshold           = 10
  alarm_description   = "ALB 5xx responses exceeded 10/min for 3 minutes"

  dimensions = {
    LoadBalancer = var.alb_arn
  }

  alarm_actions = []
  ok_actions    = []

  tags = {
    Environment = var.environment
  }
}

# CloudWatch alarm: target response time (p99 proxy)
resource "aws_cloudwatch_metric_alarm" "target_response_p99" {
  alarm_name          = "${local.name_prefix}-target-response-p99"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 3
  metric_name         = "TargetResponseTime"
  namespace           = "AWS/ApplicationELB"
  period              = 60
  statistic           = "Maximum"
  threshold           = var.p99_threshold_ms / 1000.0
  alarm_description   = "Target p99 response time exceeded ${var.p99_threshold_ms}ms"

  dimensions = {
    LoadBalancer = var.alb_arn
  }

  tags = {
    Environment = var.environment
  }
}

# CloudWatch alarm: ASG unhealthy hosts
resource "aws_cloudwatch_metric_alarm" "unhealthy_hosts" {
  alarm_name          = "${local.name_prefix}-unhealthy-hosts"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 2
  metric_name         = "UnHealthyHostCount"
  namespace           = "AWS/ApplicationELB"
  period              = 60
  statistic           = "Average"
  threshold           = 0
  alarm_description   = "ASG has unhealthy hosts — instances failing ELB health check"

  dimensions = {
    AutoScalingGroupName = var.asg_name
  }

  tags = {
    Environment = var.environment
  }
}

# CloudWatch alarm: RDS CPU
resource "aws_cloudwatch_metric_alarm" "rds_cpu" {
  alarm_name          = "${local.name_prefix}-rds-cpu-high"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 5
  metric_name         = "CPUUtilization"
  namespace           = "AWS/RDS"
  period              = 60
  statistic           = "Average"
  threshold           = 85
  alarm_description   = "RDS CPU > 85% for 5 minutes"

  dimensions = {
    DBInstanceIdentifier = var.db_instance_id
  }

  tags = {
    Environment = var.environment
  }
}

# CloudWatch alarm: RDS free storage
resource "aws_cloudwatch_metric_alarm" "rds_free_storage" {
  alarm_name          = "${local.name_prefix}-rds-low-storage"
  comparison_operator = "LessThanThreshold"
  evaluation_periods  = 2
  metric_name         = "FreeStorageSpace"
  namespace           = "AWS/RDS"
  period              = 300
  statistic           = "Average"
  threshold           = 10 * 1024 * 1024 * 1024 # 10 GB
  alarm_description   = "RDS free storage below 10GB"

  dimensions = {
    DBInstanceIdentifier = var.db_instance_id
  }

  tags = {
    Environment = var.environment
  }
}

# Custom metric: SagaExecutionLog stuck count. Published by a scheduled
# Lambda that runs sp to count non-terminal rows past the staleness
# threshold. The Lambda itself is defined in alarm-saga-stuck.tf.
resource "aws_cloudwatch_metric_alarm" "saga_stuck" {
  alarm_name          = "${local.name_prefix}-saga-stuck"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "StuckSagaCount"
  namespace           = "ApiGateway"
  period              = 300
  statistic           = "Maximum"
  threshold           = var.saga_stale_threshold
  alarm_description   = "Number of SagaExecutionLog rows stuck in non-terminal state past threshold"

  tags = {
    Environment = var.environment
  }
}
