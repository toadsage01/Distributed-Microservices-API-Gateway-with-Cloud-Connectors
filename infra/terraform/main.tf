module "network" {
  source = "./network"

  project_name = var.project_name
  environment  = var.environment
}

module "secrets" {
  source = "./secrets"

  project_name           = var.project_name
  environment             = var.environment
  jwt_signing_key         = var.jwt_signing_key
  sql_server_admin_password = var.sql_server_admin_password
}

module "database" {
  source = "./database"

  project_name            = var.project_name
  environment              = var.environment
  vpc_id                   = module.network.vpc_id
  private_subnet_ids      = module.network.private_subnet_ids
  db_security_group_id    = module.network.db_security_group_id
  sql_server_admin_password_secret_arn = module.secrets.db_password_secret_arn

  depends_on = [module.secrets]
}

module "compute" {
  source = "./compute"

  project_name        = var.project_name
  environment          = var.environment
  vpc_id               = module.network.vpc_id
  public_subnet_ids   = module.network.public_subnet_ids
  private_subnet_ids  = module.network.private_subnet_ids
  alb_security_group_id = module.network.alb_security_group_id
  app_security_group_id = module.network.app_security_group_id
  db_endpoint          = module.database.primary_endpoint
  jwt_signing_key_secret_arn = module.secrets.jwt_signing_key_secret_arn
  db_credentials_secret_arn  = module.secrets.db_credentials_secret_arn
  app_port             = var.app_port

  depends_on = [module.database]
}

module "monitoring" {
  source = "./monitoring"

  project_name     = var.project_name
  environment       = var.environment
  alb_arn           = module.compute.alb_arn
  asg_name          = module.compute.asg_name
  db_instance_id    = module.database.primary_instance_id

  depends_on = [module.compute, module.database]
}
