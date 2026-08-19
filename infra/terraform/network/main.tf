locals {
  name_prefix = "${var.project_name}-${var.environment}"
}

data "aws_availability_zones" "available" {
  state = "available"
  filter {
    name   = "zone-id"
    values = [for az in var.availability_zones : "${var.region == "" ? "use1" : var.region}${az}"]
  }
}

variable "region" {
  type    = string
  default = ""
}

# VPC
resource "aws_vpc" "main" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = {
    Name        = "${local.name_prefix}-vpc"
    Environment = var.environment
    Project     = var.project_name
  }
}

# Internet Gateway for the public subnets
resource "aws_internet_gateway" "main" {
  vpc_id = aws_vpc.main.id
  tags = {
    Name = "${local.name_prefix}-igw"
  }
}

# Elastic IP for the NAT gateway (single NAT to keep costs down —
# traffic from private subnets exits via one AZ in v1)
resource "aws_eip" "nat" {
  domain = "vpc"
  tags = {
    Name = "${local.name_prefix}-nat-eip"
  }
}

# Public subnet in each AZ — ALB lives here
resource "aws_subnet" "public" {
  count                   = length(var.availability_zones)
  vpc_id                  = aws_vpc.main.id
  cidr_block              = cidrsubnet(var.vpc_cidr, 4, count.index)
  availability_zone       = var.availability_zones[count.index]
  map_public_ip_on_launch = true

  tags = {
    Name = "${local.name_prefix}-public-${count.index}"
    Tier = "public"
  }
}

# Private subnet in each AZ — EC2 and RDS live here
resource "aws_subnet" "private" {
  count             = length(var.availability_zones)
  vpc_id            = aws_vpc.main.id
  cidr_block        = cidrsubnet(var.vpc_cidr, 4, count.index + 10)
  availability_zone = var.availability_zones[count.index]

  tags = {
    Name = "${local.name_prefix}-private-${count.index}"
    Tier = "private"
  }
}

# NAT gateway in the first public subnet
resource "aws_nat_gateway" "main" {
  allocation_id = aws_eip.nat.id
  subnet_id     = aws_subnet.public[0].id

  tags = {
    Name = "${local.name_prefix}-nat"
  }

  depends_on = [aws_internet_gateway.main]
}

# Route table for public subnets — direct 0/0 to IGW
resource "aws_route_table" "public" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.main.id
  }

  tags = {
    Name = "${local.name_prefix}-public-rt"
  }
}

resource "aws_route_table_association" "public" {
  count          = length(aws_subnet.public)
  subnet_id      = aws_subnet.public[count.index].id
  route_table_id = aws_route_table.public.id
}

# Route table for private subnets — direct 0/0 to NAT
resource "aws_route_table" "private" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block     = "0.0.0.0/0"
    nat_gateway_id = aws_nat_gateway.main.id
  }

  tags = {
    Name = "${local.name_prefix}-private-rt"
  }
}

resource "aws_route_table_association" "private" {
  count          = length(aws_subnet.private)
  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private.id
}

# Security group for the ALB — internet-facing on 443 only
resource "aws_security_group" "alb" {
  name        = "${local.name_prefix}-alb-sg"
  description = "Internet-facing ALB — TLS only"
  vpc_id      = aws_vpc.main.id

  ingress {
    description = "HTTPS from internet"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name = "${local.name_prefix}-alb-sg"
  }
}

# Security group for the EC2 app instances — only accepts traffic from
# the ALB on the app port. No direct internet ingress.
resource "aws_security_group" "app" {
  name        = "${local.name_prefix}-app-sg"
  description = "App instances — accepts traffic from ALB only"
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "App port from ALB"
    from_port       = var.app_port
    to_port         = var.app_port
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name = "${local.name_prefix}-app-sg"
  }
}

# Security group for RDS — only accepts 1433 from the app SG
resource "aws_security_group" "db" {
  name        = "${local.name_prefix}-db-sg"
  description = "RDS — SQL Server port from app SG only"
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "SQL Server from app"
    from_port       = 1433
    to_port         = 1433
    protocol        = "tcp"
    security_groups = [aws_security_group.app.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name = "${local.name_prefix}-db-sg"
  }
}

variable "app_port" {
  type    = number
  default = 8080
}
