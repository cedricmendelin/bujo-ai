# No suitable AVM consumption-budget module is published; use the AzureRM resource.
resource "azurerm_consumption_budget_resource_group" "prod" {
  name              = "bujo-prod-monthly"
  resource_group_id = data.azurerm_resource_group.prod.id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  time_period {
    start_date = "2026-11-01T00:00:00Z"
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_emails = [var.budget_alert_email]
  }

  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_emails = [var.budget_alert_email]
  }
}
