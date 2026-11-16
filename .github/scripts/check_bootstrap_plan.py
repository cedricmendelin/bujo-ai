import json
import sys


PLACEHOLDER = (
    "mcr.microsoft.com/k8se/quickstart-jobs@"
    "sha256:912ce4be4e9c4eb75cafa3f846d47d8d4ccebde1135c3059d1490d0b8c457187"
)

ALLOWED = {
    "azapi_resource.sql_database": "azapi_resource",
    "azurerm_container_app_job.migration": "azurerm_container_app_job",
    "module.container_app_environment.azapi_resource.this": "azapi_resource",
    "module.identity.azurerm_user_assigned_identity.this": "azurerm_user_assigned_identity",
    "module.log_analytics.azurerm_log_analytics_workspace.this": "azurerm_log_analytics_workspace",
    "module.migration_identity.azurerm_user_assigned_identity.this": "azurerm_user_assigned_identity",
    'module.sql_server.azurerm_mssql_firewall_rule.this["azure_services"]': "azurerm_mssql_firewall_rule",
    "module.sql_server.azurerm_mssql_server.this": "azurerm_mssql_server",
}
AZAPI_TYPES = {
    "azapi_resource.sql_database": "Microsoft.Sql/servers/databases@2023-08-01-preview",
    "module.container_app_environment.azapi_resource.this": "Microsoft.App/managedEnvironments@2025-10-02-preview",
}


def has_unknown(value) -> bool:
    if isinstance(value, dict):
        return any(has_unknown(item) for item in value.values())
    if isinstance(value, list):
        return any(has_unknown(item) for item in value)
    return value is True


def validate_plan(plan: dict) -> list[tuple[str, str]]:
    if plan.get("errored") is True or plan.get("deferred_changes"):
        raise ValueError("Bootstrap plan is errored or contains deferred changes.")
    changes = plan.get("resource_changes")
    if not isinstance(changes, list):
        raise ValueError("Plan has no resource change inventory.")

    job = None
    result = []
    for resource in changes:
        if resource.get("mode") != "managed":
            continue
        address = resource.get("address")
        change = resource.get("change") or {}
        actions = change.get("actions")
        if actions == ["no-op"] and address != "azurerm_container_app_job.migration":
            continue
        if address not in ALLOWED or resource.get("type") != ALLOWED[address]:
            raise ValueError(f"Unexpected managed resource in bootstrap plan: {address}")
        if actions not in (["create"], ["update"], ["no-op"]):
            raise ValueError(f"Unsafe resource action in bootstrap plan: {address}")
        if address in AZAPI_TYPES and (change.get("after") or {}).get("type") != AZAPI_TYPES[address]:
            raise ValueError(f"Unexpected Azure resource type in bootstrap plan: {address}")
        if address == 'module.sql_server.azurerm_mssql_firewall_rule.this["azure_services"]':
            firewall = change.get("after") or {}
            unknown = change.get("after_unknown") or {}
            if (
                firewall.get("start_ip_address") != "0.0.0.0"
                or firewall.get("end_ip_address") != "0.0.0.0"
                or has_unknown(unknown.get("start_ip_address"))
                or has_unknown(unknown.get("end_ip_address"))
            ):
                raise ValueError("Bootstrap SQL firewall must remain restricted to Azure services.")
        if address == "azurerm_container_app_job.migration":
            job = change.get("after")
            unknown = change.get("after_unknown") or {}
            for key in ("template", "schedule_trigger_config", "event_trigger_config", "manual_trigger_config", "secret", "registry"):
                if key == "template":
                    unknown_templates = unknown.get(key) or []
                    if unknown_templates is True:
                        raise ValueError("Bootstrap job template has unknown values.")
                    for template in unknown_templates:
                        if has_unknown(template.get("init_container")):
                            raise ValueError("Bootstrap job has unknown init containers.")
                        for container in template.get("container") or []:
                            if any(has_unknown(container.get(field)) for field in ("image", "command", "args", "env")):
                                raise ValueError("Bootstrap job container has unknown execution settings.")
                elif has_unknown(unknown.get(key)):
                    raise ValueError("Bootstrap job has unknown execution settings.")
        result.append((address, actions[0]))

    if not isinstance(job, dict):
        raise ValueError("Bootstrap plan does not contain the migration job.")
    templates = job.get("template") or []
    if len(templates) != 1 or len(templates[0].get("container") or []) != 1:
        raise ValueError("Bootstrap job has an unexpected container template.")
    template = templates[0]
    container = template["container"][0]
    if (
        container.get("image") != PLACEHOLDER
        or container.get("command") != ["/bootstrap-disabled"]
        or container.get("args")
        or container.get("env")
        or template.get("init_container")
        or job.get("schedule_trigger_config")
        or job.get("event_trigger_config")
        or len(job.get("manual_trigger_config") or []) != 1
        or job.get("secret")
        or job.get("registry")
        or len(job.get("identity") or []) != 1
        or job["identity"][0].get("type") != "UserAssigned"
    ):
        raise ValueError("Bootstrap job is not the inert manual placeholder.")
    return result


def main() -> None:
    try:
        plan = json.load(sys.stdin)
        resources = validate_plan(plan)
    except (ValueError, TypeError, KeyError) as error:
        print(f"Bootstrap plan rejected: {error}", file=sys.stderr)
        raise SystemExit(1) from error
    for address, action in resources:
        print(f"{action}: {address}")
    print("Resource inventory passed; the owner must still review the exact saved plan.")


if __name__ == "__main__":
    main()
