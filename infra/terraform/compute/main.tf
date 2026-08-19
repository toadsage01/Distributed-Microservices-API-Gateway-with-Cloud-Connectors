locals {
  name_prefix = "${var.project_name}-${var.environment}"
}

# IAM role for the EC2 instance profile. Scoped to only the Secrets
# Manager paths it actually needs (least privilege) plus CloudWatch
# agent actions for metrics + logs.
resource "aws_iam_role" "app" {
  name = "${local.name_prefix}-app-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ec2.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })

  tags = {
    Name = "${local.name_prefix}-app-role"
  }
}

resource "aws_iam_policy" "secrets_access" {
  name        = "${local.name_prefix}-secrets-access"
  description = "Allow reading the JWT signing key and DB credentials"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = [
          var.jwt_signing_key_secret_arn,
          var.db_credentials_secret_arn
        ]
      }
    ]
  })
}

resource "aws_iam_policy" "cloudwatch" {
  name        = "${local.name_prefix}-cloudwatch"
  description = "Allow CloudWatch agent to push metrics and logs"

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "cloudwatch:PutMetricData",
          "logs:CreateLogStream",
          "logs:PutLogEvents",
          "logs:DescribeLogStreams"
        ]
        Resource = "*"
      }
    ]
  })
}

resource "aws_iam_role_policy_attachment" "secrets" {
  role       = aws_iam_role.app.name
  policy_arn = aws_iam_policy.secrets_access.arn
}

resource "aws_iam_role_policy_attachment" "cloudwatch" {
  role       = aws_iam_role.app.name
  policy_arn = aws_iam_policy.cloudwatch.arn
}

resource "aws_iam_role_policy_attachment" "ssm" {
  role       = aws_iam_role.app.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
}

resource "aws_iam_instance_profile" "app" {
  name = "${local.name_prefix}-app-profile"
  role = aws_iam_role.app.name
}

# Launch template — user data installs .NET 8, pulls the latest build
# artifact from S3, and starts the API service.
resource "aws_launch_template" "app" {
  name          = "${local.name_prefix}-app-lt"
  image_id      = var.ami_id
  instance_type = var.instance_type

  iam_instance_profile {
    name = aws_iam_instance_profile.app.name
  }

  network_interfaces {
    associate_public_ip_address = false
    security_groups             = [var.app_security_group_id]
  }

  user_data = base64encode(templatefile("${path.module}/user-data.sh", {
    app_port                = var.app_port
    db_endpoint             = var.db_endpoint
    jwt_secret_arn          = var.jwt_signing_key_secret_arn
    db_credentials_secret_arn = var.db_credentials_secret_arn
    environment             = var.environment
  }))

  tag_specifications {
    resource_type = "instance"
    tags = {
      Name        = "${local.name_prefix}-app"
      Environment = var.environment
    }
  }
}

# ALB — internet-facing on 443, forwards to the app port on the instances
resource "aws_lb" "main" {
  name               = "${local.name_prefix}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [var.alb_security_group_id]
  subnets            = var.public_subnet_ids

  tags = {
    Name = "${local.name_prefix}-alb"
  }
}

resource "aws_lb_target_group" "app" {
  name        = "${local.name_prefix}-app-tg"
  port        = var.app_port
  protocol    = "HTTP"
  vpc_id      = var.vpc_id
  target_type = "instance"

  health_check {
    enabled             = true
    path                = "/health/ready"
    port                = "traffic-port"
    healthy_threshold   = 2
    unhealthy_threshold = 3
    timeout             = 5
    interval            = 15
    matcher             = "200"
  }

  stickiness {
    type    = "lb_cookie"
    enabled = false
  }

  tags = {
    Name = "${local.name_prefix}-app-tg"
  }
}

# TLS certificate — requires ACM to be pre-provisioned and DNS validated.
# Using a data source here so the cert isn't managed by Terraform (it
# outlives stack destruction in prod).
data "aws_acm_certificate" "main" {
  domain      = "${var.project_name}-${var.environment}.${data.aws_caller_identity.current.account_id}.example.com"
  statuses    = ["ISSUED"]
  most_recent = true
}

data "aws_caller_identity" "current" {}

resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.main.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = data.aws_acm_certificate.main.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.app.arn
  }
}

# Target tracking scaling policy — keeps CPU around 60%, plus scales on
# request count per target to catch traffic bursts before CPU spikes.
resource "aws_autoscaling_group" "app" {
  name                = "${local.name_prefix}-asg"
  vpc_zone_identifier = var.private_subnet_ids
  min_size            = var.asg_min_size
  max_size            = var.asg_max_size
  desired_capacity    = var.asg_desired_capacity

  launch_template {
    id      = aws_launch_template.app.id
    version = "$Latest"
  }

  target_group_arns = [aws_lb_target_group.app.arn]

  health_check_type         = "ELB"
  health_check_grace_period = 60

  tag {
    key                 = "Name"
    value               = "${local.name_prefix}-app"
    propagate_at_launch = true
  }
  tag {
    key                 = "Environment"
    value               = var.environment
    propagate_at_launch = true
  }
}

resource "aws_autoscaling_policy" "cpu" {
  name                   = "${local.name_prefix}-cpu-policy"
  autoscaling_group_name = aws_autoscaling_group.app.name
  policy_type            = "TargetTrackingScaling"

  target_tracking_configuration {
    predefined_metric_specification {
      predefined_metric_type = "ASGAverageCPUUtilization"
    }
    target_value = 60.0
  }
}

resource "aws_autoscaling_policy" "requests" {
  name                   = "${local.name_prefix}-requests-policy"
  autoscaling_group_name = aws_autoscaling_group.app.name
  policy_type            = "TargetTrackingScaling"

  target_tracking_configuration {
    customized_metric_specification {
      metric_name = "RequestCountPerTarget"
      namespace   = "AWS/ApplicationELB"
      statistic   = "Sum"
      dimensions {
        name  = "LoadBalancer"
        value = aws_lb.main.arn_suffix
      }
      dimensions {
        name  = "TargetGroup"
        value = aws_lb_target_group.app.arn_suffix
      }
    }
    target_value = 1000.0
  }
}
