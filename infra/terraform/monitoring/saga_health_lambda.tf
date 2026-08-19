# Lambda that runs every 5 minutes, queries SagaExecutionLog for non-terminal
# rows past the staleness threshold, and publishes the count as a custom
# CloudWatch metric. The alarm above fires on this metric.

data "aws_iam_policy_document" "lambda_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "saga_health" {
  name               = "${var.project_name}-${var.environment}-saga-health"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
}

resource "aws_iam_policy" "saga_health" {
  name = "${var.project_name}-${var.environment}-saga-health"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "cloudwatch:PutMetricData"
        ]
        Resource = "*"
      },
      {
        Effect = "Allow"
        Action = [
          "secretsmanager:GetSecretValue"
        ]
        Resource = ["arn:aws:secretsmanager:*:*:secret:${var.project_name}-${var.environment}/db-credentials-*"]
      },
      {
        Effect = "Allow"
        Action = [
          "logs:CreateLogStream",
          "logs:PutLogEvents"
        ]
        Resource = "*"
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "saga_health" {
  role       = aws_iam_role.saga_health.name
  policy_arn = aws_iam_policy.saga_health.arn
}

# Lambda function — the source is a small Python script packaged inline
# via the zip packaging.
resource "aws_lambda_function" "saga_health" {
  filename         = data.archive_file.saga_health.output_path
  source_code_hash = data.archive_file.saga_health.output_base64sha256
  function_name    = "${var.project_name}-${var.environment}-saga-health"
  role             = aws_iam_role.saga_health.arn
  handler          = "handler.lambda_handler"
  runtime          = "python3.12"
  timeout          = 30
  memory_size      = 256

  environment {
    variables = {
      DB_SECRET_ARN = "arn:aws:secretsmanager:${data.aws_caller_identity.current.region}:${data.aws_caller_identity.current.account_id}:secret:${var.project_name}-${var.environment}/db-credentials-composite"
      METRIC_NAMESPACE = "ApiGateway"
    }
  }

  tags = {
    Environment = var.environment
  }
}

data "archive_file" "saga_health" {
  type        = "zip"
  output_path = "${path.module}/saga_health.zip"
  source {
    content  = file("${path.module}/saga_health_lambda.py")
    filename = "handler.py"
  }
}

# Schedule every 5 minutes
resource "aws_cloudwatch_event_rule" "saga_health" {
  name                = "${var.project_name}-${var.environment}-saga-health"
  description         = "Scheduled trigger for the saga health Lambda"
  schedule_expression = "rate(5 minutes)"
}

resource "aws_cloudwatch_event_target" "saga_health" {
  rule      = aws_cloudwatch_event_rule.saga_health.name
  target_id = "saga-health-lambda"
  arn       = aws_lambda_function.saga_health.arn
}

resource "aws_lambda_permission" "saga_health" {
  statement_id  = "AllowExecutionFromCloudWatch"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.saga_health.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.saga_health.arn
}

data "aws_caller_identity" "current" {}
