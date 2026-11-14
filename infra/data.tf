# The production resource group is created by the owner as part of bootstrap (#24).
data "azurerm_resource_group" "prod" {
  name = var.resource_group_name
}
