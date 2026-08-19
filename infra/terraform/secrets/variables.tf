variable "project_name" { type = string }
variable "environment" { type = string }
variable "jwt_signing_key" {
  type      = string
  sensitive = true
}
variable "sql_server_admin_password" {
  type      = string
  sensitive = true
}
