import json
import re
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

# Full-root infrastructure baseline inventory. Keep this separate from ALLOWED:
# validate_plan remains the legacy, job-only bootstrap contract.
BASELINE_ALLOWED = {
    "azapi_resource.sql_database": "azapi_resource",
    "azuread_application.api": "azuread_application",
    "azuread_service_principal.api": "azuread_service_principal",
    "azurerm_consumption_budget_resource_group.prod": "azurerm_consumption_budget_resource_group",
    "azurerm_container_app_job.migration": "azurerm_container_app_job",
    'module.container_app.azapi_resource.container_app': "azapi_resource",
    "module.container_app.azapi_resource_action.post_creation_update": "azapi_resource_action",
    "module.container_app.terraform_data.update_keeper": "terraform_data",
    "module.container_app_environment.azapi_resource.this": "azapi_resource",
    "module.identity.azurerm_user_assigned_identity.this": "azurerm_user_assigned_identity",
    "module.key_vault.azapi_resource.this": "azapi_resource",
    "module.log_analytics.azurerm_log_analytics_workspace.this": "azurerm_log_analytics_workspace",
    "module.log_analytics.time_sleep.wait_for_ampls_update": "time_sleep",
    "module.migration_identity.azurerm_user_assigned_identity.this": "azurerm_user_assigned_identity",
    'module.sql_server.azurerm_mssql_firewall_rule.this["azure_services"]': "azurerm_mssql_firewall_rule",
    "module.sql_server.azurerm_mssql_server.this": "azurerm_mssql_server",
    "random_uuid.api_scope": "random_uuid",
    "random_uuid.api_user_role": "random_uuid",
}

BASELINE_AZAPI_TYPES = {
    "azapi_resource.sql_database": "Microsoft.Sql/servers/databases@2023-08-01-preview",
    "module.container_app.azapi_resource.container_app": "Microsoft.App/containerApps@2025-02-02-preview",
    "module.container_app_environment.azapi_resource.this": "Microsoft.App/managedEnvironments@2025-10-02-preview",
    "module.key_vault.azapi_resource.this": "Microsoft.KeyVault/vaults@2026-02-01",
}

# Terraform 1.17.0's actual AVM mock plan reports these provider-computed paths.
# Input paths are intentionally not wildcarded: a newly unknown image, SQL limit,
# owner, ingress property, or application setting must be reviewed as a code change.
BASELINE_UNKNOWN_PATHS = {
    "azapi_resource.sql_database": {
        "id", "ignore_casing", "ignore_missing_property", "ignore_null_property",
        "output", "parent_id", "schema_validation_enabled",
    },
    "azuread_application.api": {
        "api[0].oauth2_permission_scope[0].id", "app_role[0].id", "app_role_ids",
        "client_id", "disabled_by_microsoft", "id", "logo_url",
        "oauth2_permission_scope_ids", "object_id", "publisher_domain", "tags",
        "template_id",
    },
    "azuread_service_principal.api": {
        "app_role_ids", "app_roles", "application_tenant_id", "client_id",
        "display_name", "homepage_url", "id", "logout_url",
        "oauth2_permission_scope_ids", "oauth2_permission_scopes", "object_id",
        "redirect_uris", "saml_metadata_url", "service_principal_names",
        "sign_in_audience", "tags", "type",
    },
    "azurerm_consumption_budget_resource_group.prod": {
        "etag", "id", "time_period[0].end_date",
    },
    "azurerm_container_app_job.migration": {
        "container_app_environment_id", "event_stream_endpoint", "id",
        "identity[0].identity_ids[0]", "identity[0].principal_id",
        "identity[0].tenant_id", "outbound_ip_addresses",
        "template[0].container[0].ephemeral_storage",
    },
    "module.container_app.azapi_resource.container_app": {
        "body.properties.environmentId", "body.properties.managedEnvironmentId",
        "body.properties.template.containers[0].env[0].value",
        "body.properties.template.containers[0].env[3]", "id",
        "identity[0].identity_ids[0]", "identity[0].principal_id",
        "identity[0].tenant_id", "ignore_casing", "ignore_missing_property",
        "output",
    },
    "module.container_app.azapi_resource_action.post_creation_update": {
        "body.properties.template.containers[0].env[0].value",
        "body.properties.template.containers[0].env[3]", "exist", "id",
        "output", "resource_id", "retry.max_interval_seconds", "retry.multiplier",
        "retry.randomization_factor", "sensitive_output", "when",
    },
    "module.container_app.terraform_data.update_keeper": {
        "id", "input.containers[0].env[0].value",
        "input.containers[0].env[3].value", "output",
    },
    "module.container_app_environment.azapi_resource.this": {
        "body.properties.appLogsConfiguration.logAnalyticsConfiguration", "id",
        "ignore_casing", "ignore_missing_property", "output",
        "retry.interval_seconds", "retry.max_interval_seconds",
        "retry.multiplier", "retry.randomization_factor",
    },
    "module.identity.azurerm_user_assigned_identity.this": {
        "client_id", "id", "principal_id", "tenant_id",
    },
    "module.key_vault.azapi_resource.this": {
        "id", "ignore_casing", "ignore_missing_property", "output",
        "schema_validation_enabled",
    },
    "module.log_analytics.azurerm_log_analytics_workspace.this": {
        "id", "local_authentication_disabled", "primary_shared_key",
        "secondary_shared_key", "workspace_id",
    },
    "module.log_analytics.time_sleep.wait_for_ampls_update": {"id"},
    "module.migration_identity.azurerm_user_assigned_identity.this": {
        "client_id", "id", "principal_id", "tenant_id",
    },
    'module.sql_server.azurerm_mssql_firewall_rule.this["azure_services"]': {
        "id", "server_id",
    },
    "module.sql_server.azurerm_mssql_server.this": {
        "administrator_login", "fully_qualified_domain_name", "id",
        "identity[0].principal_id", "identity[0].tenant_id",
        "primary_user_assigned_identity_id", "restorable_dropped_database_ids",
    },
    "random_uuid.api_scope": {"id", "result"},
    "random_uuid.api_user_role": {"id", "result"},
}

BASELINE_JOB_IMAGE = (
    "mcr.microsoft.com/k8se/quickstart-jobs@"
    "sha256:912ce4be4e9c4eb75cafa3f846d47d8d4ccebde1135c3059d1490d0b8c457187"
)


def has_unknown(value) -> bool:
    if isinstance(value, dict):
        return any(has_unknown(item) for item in value.values())
    if isinstance(value, list):
        return any(has_unknown(item) for item in value)
    return value is True


def _unknown_paths(value, path=""):
    if value is True:
        yield path
    elif isinstance(value, dict):
        for key, item in value.items():
            yield from _unknown_paths(item, f"{path}.{key}" if path else key)
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from _unknown_paths(item, f"{path}[{index}]")


def _value(resource, key="after"):
    change = resource.get("change") or {}
    return change.get(key) if isinstance(change.get(key), dict) else {}


def _assert_unknowns_are_bounded(resource):
    address = resource["address"]
    actual = set(_unknown_paths((resource.get("change") or {}).get("after_unknown")))
    allowed = BASELINE_UNKNOWN_PATHS.get(address, set())
    if actual - allowed:
        raise ValueError("Baseline has unknown input values outside approved computed dependencies.")


def _require_exactly_one(items, label):
    if not isinstance(items, list) or len(items) != 1 or not isinstance(items[0], dict):
        raise ValueError(f"Baseline {label} must contain exactly one configured item.")
    return items[0]


def _require_dependency(relevant, resource, attribute):
    if (resource, tuple(attribute)) not in relevant:
        raise ValueError("Baseline plan is missing an approved computed-value dependency.")


def _baseline_configuration(plan, expected_image, expected_owner, expected_app_id):
    if not all(isinstance(value, str) and value.strip() for value in (expected_image, expected_owner, expected_app_id)):
        raise ValueError("Baseline image, owner, and app target evidence are required.")

    changes = plan.get("resource_changes")
    if not isinstance(changes, list):
        raise ValueError("Baseline plan has no resource change inventory.")

    resources = {}
    result = []
    for item in changes:
        if item.get("mode") != "managed":
            continue
        address = item.get("address")
        change = item.get("change") or {}
        actions = change.get("actions")
        if not isinstance(address, str) or address not in BASELINE_ALLOWED or item.get("type") != BASELINE_ALLOWED[address]:
            raise ValueError("Baseline contains an unexpected managed resource.")
        if actions not in (["create"], ["update"], ["no-op"], ["delete", "create"]):
            raise ValueError("Baseline contains an unsafe resource action.")
        if actions == ["delete", "create"] and (
            address != "module.container_app.azapi_resource_action.post_creation_update"
            or item.get("action_reason") != "replace_by_triggers"
        ):
            raise ValueError("Baseline contains a destroy or unapproved replacement.")
        if address in BASELINE_AZAPI_TYPES and (_value(item).get("type") != BASELINE_AZAPI_TYPES[address]):
            raise ValueError("Baseline contains an unexpected Azure resource type.")
        _assert_unknowns_are_bounded(item)
        if address in resources:
            raise ValueError("Baseline contains a duplicate managed resource.")
        resources[address] = item
        if actions != ["no-op"] or address == "azurerm_container_app_job.migration":
            result.append((address, actions[0]))

    required = set(BASELINE_ALLOWED)
    if required - resources.keys():
        raise ValueError("Baseline plan is missing one or more reviewed resources.")

    application = _value(resources["azuread_application.api"])
    service_principal = _value(resources["azuread_service_principal.api"])
    if (
        [str(owner).lower() for owner in application.get("owners") or []] != [expected_owner.lower()]
        or [str(owner).lower() for owner in service_principal.get("owners") or []] != [expected_owner.lower()]
    ):
        raise ValueError("API application and service principal must be owned by the intended deployment principal.")
    if (
        application.get("display_name") is None
        or not str(application.get("display_name")).startswith("app-")
        or not str(application.get("display_name")).endswith("-api")
        or application.get("sign_in_audience") != "AzureADMyOrg"
        or application.get("prevent_duplicate_names") is not True
        or application.get("required_resource_access") not in (None, [])
        or application.get("password") not in (None, [])
    ):
        raise ValueError("API application has unexpected ownership, audience, Graph permissions, or credentials.")
    api = _require_exactly_one(application.get("api"), "API registration")
    scopes = api.get("oauth2_permission_scope") or []
    roles = application.get("app_role") or []
    display_name = application.get("display_name") or ""
    suffix_match = re.fullmatch(r"app-([a-z0-9]{4,8})-api", display_name)
    if (
        api.get("requested_access_token_version") != 2
        or api.get("known_client_applications") not in (None, [])
        or len(scopes) != 1
        or scopes[0].get("value") != "journal.access"
        or scopes[0].get("type") != "User"
        or scopes[0].get("enabled") is not True
        or suffix_match is None
        or application.get("identifier_uris") != [f"api://bujo-ai-{suffix_match.group(1)}"]
    ):
        raise ValueError("API registration has an unexpected delegated scope.")
    if (
        len(roles) != 1
        or roles[0].get("value") != "JournalUser"
        or roles[0].get("allowed_member_types") != ["User"]
        or roles[0].get("enabled") is not True
    ):
        raise ValueError("API registration has unexpected application roles.")
    if service_principal.get("app_role_assignment_required") is not True or service_principal.get("use_existing") is True:
        raise ValueError("API service principal assignment or ownership settings are unsafe.")
    if application.get("client_id") is not None and service_principal.get("client_id") is not None:
        if application["client_id"] != service_principal["client_id"]:
            raise ValueError("API service principal is not bound to the managed application.")

    # Unknown scope/role IDs and the API audience are accepted only when the
    # emitted plan names their concrete producing resources as dependencies.
    relevant = {
        (item.get("resource"), tuple(item.get("attribute") or []))
        for item in plan.get("relevant_attributes", [])
    }
    application_unknown = set(_unknown_paths((resources["azuread_application.api"].get("change") or {}).get("after_unknown")))
    dependency_pairs = (
        ("api[0].oauth2_permission_scope[0].id", "random_uuid.api_scope"),
        ("app_role[0].id", "random_uuid.api_user_role"),
    )
    for unknown_path, producer in dependency_pairs:
        if unknown_path in application_unknown and (producer, ("result",)) not in relevant:
            raise ValueError("Baseline plan is missing an approved API identifier dependency.")
    for unknown_path, identifier in (
        ("api[0].oauth2_permission_scope[0].id", scopes[0].get("id")),
        ("app_role[0].id", roles[0].get("id")),
    ):
        if identifier is None:
            if unknown_path not in application_unknown:
                raise ValueError("Baseline API role and scope identifiers must be computed UUIDs.")
        elif not re.fullmatch(r"[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", str(identifier)):
            raise ValueError("Baseline API role and scope identifiers must be UUIDs.")
    for address, identifier in (
        ("random_uuid.api_scope", scopes[0].get("id")),
        ("random_uuid.api_user_role", roles[0].get("id")),
    ):
        generated = _value(resources[address]).get("result")
        if identifier is not None and generated is not None and identifier != generated:
            raise ValueError("Baseline API identifier is not bound to its root-generated UUID.")
    if "client_id" in set(_unknown_paths((resources["azuread_service_principal.api"].get("change") or {}).get("after_unknown"))):
        _require_dependency(relevant, "azuread_application.api", ("client_id",))

    database_unknown = set(_unknown_paths((resources["azapi_resource.sql_database"].get("change") or {}).get("after_unknown")))
    if "parent_id" in database_unknown:
        _require_dependency(relevant, "module.sql_server.azurerm_mssql_server.this", ("id",))

    job_address = "azurerm_container_app_job.migration"
    job_unknown = set(_unknown_paths((resources[job_address].get("change") or {}).get("after_unknown")))
    if "container_app_environment_id" in job_unknown:
        _require_dependency(relevant, "module.container_app_environment.azapi_resource.this", ("id",))
    if "identity[0].identity_ids[0]" in job_unknown:
        _require_dependency(relevant, "module.migration_identity.azurerm_user_assigned_identity.this", ("id",))

    app_address = "module.container_app.azapi_resource.container_app"
    app_unknown = set(_unknown_paths((resources[app_address].get("change") or {}).get("after_unknown")))
    if "body.properties.environmentId" in app_unknown or "body.properties.managedEnvironmentId" in app_unknown:
        _require_dependency(relevant, "module.container_app_environment.azapi_resource.this", ("id",))
    if "identity[0].identity_ids[0]" in app_unknown:
        _require_dependency(relevant, "module.identity.azurerm_user_assigned_identity.this", ("id",))
    if "body.properties.template.containers[0].env[0].value" in app_unknown:
        _require_dependency(relevant, "module.identity.azurerm_user_assigned_identity.this", ("client_id",))
    if "body.properties.template.containers[0].env[3]" in app_unknown:
        _require_dependency(relevant, "azuread_application.api", ("client_id",))
    if (
        "body.properties.appLogsConfiguration.logAnalyticsConfiguration" in
        set(_unknown_paths((resources["module.container_app_environment.azapi_resource.this"].get("change") or {}).get("after_unknown")))
    ):
        _require_dependency(
            relevant,
            "module.container_app_environment.ephemeral.azapi_resource_action.shared_keys[0]",
            ("output",),
        )

    keeper_unknown = set(_unknown_paths((resources["module.container_app.terraform_data.update_keeper"].get("change") or {}).get("after_unknown")))
    if "input.containers[0].env[0].value" in keeper_unknown:
        _require_dependency(relevant, "module.identity.azurerm_user_assigned_identity.this", ("client_id",))
    if "input.containers[0].env[3].value" in keeper_unknown:
        _require_dependency(relevant, "azuread_application.api", ("client_id",))

    database = _value(resources["azapi_resource.sql_database"])
    db_body = database.get("body") or {}
    db_properties = db_body.get("properties") or {}
    if (
        database.get("name") != "bujo"
        or db_body.get("sku") != {"name": "GP_S_Gen5", "tier": "GeneralPurpose", "family": "Gen5", "capacity": 2}
        or db_properties.get("maxSizeBytes") != 34359738368
        or db_properties.get("minCapacity") != 0.5
        or db_properties.get("autoPauseDelay") != 60
        or db_properties.get("useFreeLimit") is not True
        or db_properties.get("freeLimitExhaustionBehavior") != "AutoPause"
    ):
        raise ValueError("Baseline SQL database must retain its free offer, size limit, and auto-pause.")

    firewall = _value(resources['module.sql_server.azurerm_mssql_firewall_rule.this["azure_services"]'])
    if (
        firewall.get("start_ip_address") != "0.0.0.0"
        or firewall.get("end_ip_address") != "0.0.0.0"
    ):
        raise ValueError("Baseline SQL firewall must remain restricted to Azure services.")
    sql_server = _value(resources["module.sql_server.azurerm_mssql_server.this"])
    admin = _require_exactly_one(sql_server.get("azuread_administrator"), "SQL Entra administrator")
    server_before = _value(resources["module.sql_server.azurerm_mssql_server.this"], "before")
    if (
        server_before.get("azuread_administrator")
        and server_before["azuread_administrator"] != sql_server.get("azuread_administrator")
    ):
        raise ValueError("Baseline SQL administrator identity changed.")
    if (
        admin.get("azuread_authentication_only") is not True
        or sql_server.get("public_network_access_enabled") is not True
        or sql_server.get("version") != "12.0"
        or len(sql_server.get("identity") or []) != 1
        or sql_server["identity"][0].get("type") != "SystemAssigned"
        or sql_server.get("administrator_login_password") is not None
        or sql_server.get("administrator_login_password_wo") is not None
    ):
        raise ValueError("Baseline SQL server must remain Entra-only and passwordless.")

    budget = _value(resources["azurerm_consumption_budget_resource_group.prod"])
    notifications = budget.get("notification") or []
    if (
        budget.get("amount") != 50
        or budget.get("time_grain") != "Monthly"
        or len(notifications) != 2
        or {
            (item.get("threshold"), item.get("operator"), item.get("threshold_type"))
            for item in notifications
        } != {(80, "GreaterThan", "Forecasted"), (100, "GreaterThan", "Actual")}
        or any(item.get("enabled") is not True for item in notifications)
    ):
        raise ValueError("Baseline monthly cost budget must stay capped at $50 with both alerts enabled.")

    workspace = _value(resources["module.log_analytics.azurerm_log_analytics_workspace.this"])
    if (
        workspace.get("daily_quota_gb") != 0.1
        or workspace.get("retention_in_days") != 30
        or workspace.get("sku") != "PerGB2018"
        or workspace.get("local_authentication_enabled") is not True
    ):
        raise ValueError("Baseline Log Analytics limits or authentication settings changed.")

    key_vault = _value(resources["module.key_vault.azapi_resource.this"])
    vault_properties = ((key_vault.get("body") or {}).get("properties") or {})
    if (
        vault_properties.get("enableRbacAuthorization") is not True
        or vault_properties.get("enablePurgeProtection") is not True
        or vault_properties.get("enableSoftDelete") is not True
        or vault_properties.get("softDeleteRetentionInDays") != 7
        or (vault_properties.get("sku") or {}).get("name") != "standard"
        or key_vault.get("sensitive_body") not in (None, {})
    ):
        raise ValueError("Baseline Key Vault must retain the standard, RBAC-enabled, secret-free configuration.")

    environment_resource = _value(resources["module.container_app_environment.azapi_resource.this"])
    environment_properties = ((environment_resource.get("body") or {}).get("properties") or {})
    vnet = environment_properties.get("vnetConfiguration")
    if (
        environment_properties.get("zoneRedundant") is not False
        or (isinstance(vnet, dict) and (
            vnet.get("infrastructureSubnetId")
            or vnet.get("internal") is True
        ))
    ):
        raise ValueError("Baseline Container Apps environment must not add private networking.")

    app_resource = resources[app_address]
    app = _value(app_resource)
    app_before = _value(app_resource, "before")
    app_body = app.get("body") or {}
    properties = app_body.get("properties") or {}
    expected_target = expected_app_id.rstrip("/")
    known_ids = [
        identifier
        for identifier in (app_before.get("id"), app.get("id"))
        if isinstance(identifier, str) and identifier
    ]
    if any(identifier.rstrip("/").lower() != expected_target.lower() for identifier in known_ids):
        raise ValueError("Baseline plan targets a different existing Container App.")
    parent_id = app.get("parent_id")
    app_name = app.get("name")
    if not isinstance(parent_id, str) or not isinstance(app_name, str):
        raise ValueError("Baseline Container App target is not fully bound.")
    configured_target = f"{parent_id.rstrip('/')}/providers/Microsoft.App/containerApps/{app_name}"
    if configured_target.lower() != expected_target.lower():
        raise ValueError("Baseline Container App name and resource group do not match the live app target.")

    outputs = plan.get("output_changes")
    if not isinstance(outputs, dict):
        raise ValueError("Baseline plan is missing required output evidence.")
    client_output = outputs.get("api_application_client_id")
    app_id_output = outputs.get("container_app_id")
    if not isinstance(client_output, dict) or not isinstance(app_id_output, dict):
        raise ValueError("Baseline plan is missing required application output evidence.")
    if has_unknown(client_output.get("after_unknown")):
        _require_dependency(relevant, "azuread_application.api", ("client_id",))
    elif client_output.get("after") != application.get("client_id"):
        raise ValueError("Baseline API client ID output is not bound to the managed registration.")
    if has_unknown(app_id_output.get("after_unknown")):
        _require_dependency(relevant, app_address, ("id",))
    elif not isinstance(app_id_output.get("after"), str) or app_id_output["after"].rstrip("/").lower() != expected_target.lower():
        raise ValueError("Baseline Container App ID output is not bound to the exact live app.")

    template = properties.get("template") or {}
    containers = template.get("containers") or []
    container = _require_exactly_one(containers, "Container App container")
    container_resources = container.get("resources") or {}
    if (
        container.get("name") != "bujo-ai"
        or container.get("image") != expected_image
        or container_resources.get("cpu") != 0.25
        or container_resources.get("memory") != "0.5Gi"
        or container.get("command") not in (None, [])
        or container.get("args") not in (None, [])
        or template.get("initContainers") not in (None, [])
    ):
        raise ValueError("Baseline must retain the exact existing image in one unchanged application container.")

    environment = container.get("env") or []
    expected_env_names = [
        "ConnectionStrings__journal",
        "Deployment__VerifyDatabaseOnStart",
        "Auth__TenantId",
        "Auth__Audience",
        "Auth__AllowedPrincipals",
    ]
    if len(environment) != len(expected_env_names):
        raise ValueError("Baseline application environment has unexpected, missing, or reordered settings.")
    environment_names = [
        expected_env_names[index] if item is None and index == 3 else item.get("name")
        for index, item in enumerate(environment)
    ]
    if environment_names != expected_env_names:
        raise ValueError("Baseline application environment has unexpected, missing, or reordered settings.")
    sql_connection = environment[0].get("value")
    if sql_connection is None and ("module.identity.azurerm_user_assigned_identity.this", ("client_id",)) not in relevant:
        raise ValueError("Baseline SQL connection target is not bound to the runtime identity.")
    if sql_connection is not None and (
        not isinstance(sql_connection, str)
        or "Initial Catalog=bujo;" not in sql_connection
        or "Authentication=Active Directory Managed Identity" not in sql_connection
        or "Password=" in sql_connection
    ):
        raise ValueError("Baseline application SQL setting is not the expected passwordless database target.")
    if sql_connection is not None:
        runtime_client_id = _value(resources["module.identity.azurerm_user_assigned_identity.this"]).get("client_id")
        sql_server_name = _value(resources["module.sql_server.azurerm_mssql_server.this"]).get("name")
        if runtime_client_id and sql_server_name:
            expected_connection = (
                f"Server=tcp:{sql_server_name}.database.windows.net,1433;Initial Catalog=bujo;Encrypt=True;"
                f"TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id={runtime_client_id}"
            )
            if sql_connection != expected_connection:
                raise ValueError("Baseline application SQL setting is not bound to the approved runtime identity.")
    if environment[1].get("value") != "true":
        raise ValueError("Baseline database startup verification must remain enabled.")
    tenant_id = environment[2].get("value")
    if tenant_id is not None and not re.fullmatch(r"[0-9a-fA-F-]{36}", str(tenant_id)):
        raise ValueError("Baseline authentication tenant setting is invalid.")
    if tenant_id and admin.get("tenant_id") and tenant_id.lower() != admin["tenant_id"].lower():
        raise ValueError("Baseline SQL administrator must remain in the application tenant.")
    audience_value = environment[3].get("value") if isinstance(environment[3], dict) else None
    app_unknown_paths = set(_unknown_paths((app_resource.get("change") or {}).get("after_unknown")))
    audience_unknown = "body.properties.template.containers[0].env[3]" in app_unknown_paths
    if audience_value is None and (
        not audience_unknown
        or ("azuread_application.api", ("client_id",)) not in relevant
    ):
        raise ValueError("Baseline authentication audience is not bound to the managed API registration.")
    audience = audience_value
    if audience is not None and application.get("client_id") is not None and audience != application["client_id"]:
        raise ValueError("Baseline authentication audience is not bound to the managed API registration.")
    if environment[4] is None or environment[4].get("value") != "":
        raise ValueError("Baseline authorization must be empty and deny all callers until owner setup.")
    if any(isinstance(item, dict) and (item.get("secretRef") or item.get("secret_name")) for item in environment):
        raise ValueError("Baseline app settings must not inject secrets.")

    app_configuration = properties.get("configuration") or {}
    ingress = app_configuration.get("ingress") or {}
    scale = template.get("scale") or {}
    app_identity = app.get("identity") or []
    if (
        app_configuration.get("activeRevisionsMode") != "Single"
        or ingress.get("external") is not True
        or ingress.get("allowInsecure") is not False
        or ingress.get("targetPort") != 8080
        or ingress.get("transport") != "Auto"
        or ingress.get("traffic") != [{"label": None, "latestRevision": True, "revisionName": None, "weight": 100}]
        or scale.get("minReplicas") != 0
        or scale.get("maxReplicas") != 1
        or app_configuration.get("secrets") not in (None, [])
        or app.get("sensitive_body") not in (None, {})
        or len(app_identity) != 1
        or app_identity[0].get("type") != "UserAssigned"
    ):
        raise ValueError("Baseline Container App identity, ingress, or revision settings changed.")
    environment_id = environment_resource.get("id")
    app_environment_ids = {
        value
        for value in (properties.get("environmentId"), properties.get("managedEnvironmentId"))
        if isinstance(value, str)
    }
    if environment_id and app_environment_ids and app_environment_ids != {environment_id}:
        raise ValueError("Baseline Container App is not bound to the approved managed environment.")
    runtime_identity_id = _value(resources["module.identity.azurerm_user_assigned_identity.this"]).get("id")
    configured_runtime_ids = app_identity[0].get("identity_ids") or []
    if runtime_identity_id and configured_runtime_ids and configured_runtime_ids != [runtime_identity_id]:
        raise ValueError("Baseline Container App identity is not bound to the runtime identity.")

    keeper = _value(resources["module.container_app.terraform_data.update_keeper"])
    keeper_template = keeper.get("input") or {}
    keeper_containers = keeper_template.get("containers") or []
    keeper_container = _require_exactly_one(keeper_containers, "Container App update keeper")
    if keeper_container.get("image") != expected_image or keeper_container.get("name") != "bujo-ai":
        raise ValueError("Baseline AVM update keeper must retain the exact existing image.")
    if (
        len(keeper_containers) != 1
        or keeper_container.get("cpu") != 0.25
        or keeper_container.get("memory") != "0.5Gi"
        or keeper_template.get("min_replicas") != 0
        or keeper_template.get("max_replicas") != 1
        or keeper_template.get("init_containers") not in (None, [])
    ):
        raise ValueError("Baseline AVM update keeper has unexpected sidecars, sizing, or scale settings.")
    keeper_env = keeper_container.get("env") or []
    if (
        [item.get("name") for item in keeper_env if isinstance(item, dict)] != expected_env_names
        or keeper_env[1].get("value") != "true"
        or keeper_env[4].get("value") != ""
        or any(item.get("secret_name") for item in keeper_env if isinstance(item, dict))
    ):
        raise ValueError("Baseline AVM update keeper has unexpected authentication or secret settings.")

    patch = _value(resources["module.container_app.azapi_resource_action.post_creation_update"])
    patch_before = _value(resources["module.container_app.azapi_resource_action.post_creation_update"], "before")
    if (
        patch.get("method") != "PATCH"
        or patch.get("type") != BASELINE_AZAPI_TYPES[app_address]
        or (patch_before.get("resource_id") and patch_before.get("resource_id").rstrip("/").lower() != expected_target.lower())
    ):
        raise ValueError("Baseline AVM app update must be a PATCH to the exact existing app.")
    patch_containers = (((patch.get("body") or {}).get("properties") or {}).get("template") or {}).get("containers") or []
    patch_container = _require_exactly_one(patch_containers, "Container App PATCH container")
    patch_resources = patch_container.get("resources") or {}
    patch_environment = patch_container.get("env") or []
    patch_names = [
        expected_env_names[index] if item is None and index == 3 else (
            item.get("name") if isinstance(item, dict) else None
        )
        for index, item in enumerate(patch_environment)
    ]
    if (
        patch_container.get("name") != "bujo-ai"
        or patch_container.get("image") != expected_image
        or patch_resources.get("cpu") != 0.25
        or patch_resources.get("memory") != "0.5Gi"
        or len(patch_environment) != len(expected_env_names)
        or patch_names != expected_env_names
        or patch_environment[1].get("value") != "true"
        or patch_environment[4].get("value") != ""
        or (isinstance(patch_environment[3], dict) and (
            patch_environment[3].get("value") not in (None, application.get("client_id"))
        ))
        or any(
            isinstance(item, dict) and (item.get("secretRef") or item.get("secret_name"))
            for item in patch_environment
        )
        or patch.get("sensitive_body") not in (None, {})
    ):
        raise ValueError("Baseline AVM PATCH must preserve the exact safe application settings.")
    patch_unknown = set(_unknown_paths((resources["module.container_app.azapi_resource_action.post_creation_update"].get("change") or {}).get("after_unknown")))
    if "resource_id" in patch_unknown:
        _require_dependency(relevant, app_address, ("id",))
    if "body.properties.template.containers[0].env[0].value" in patch_unknown:
        _require_dependency(relevant, "module.identity.azurerm_user_assigned_identity.this", ("client_id",))
    if "body.properties.template.containers[0].env[3]" in patch_unknown:
        _require_dependency(relevant, "azuread_application.api", ("client_id",))

    job = _value(resources["azurerm_container_app_job.migration"])
    templates = job.get("template") or []
    if len(templates) != 1 or not isinstance(templates[0], dict):
        raise ValueError("Baseline migration job has an unexpected template.")
    job_template = templates[0]
    job_container = _require_exactly_one(job_template.get("container"), "migration job container")
    if (
        not re.fullmatch(r"job-[a-z0-9]{4,8}-migrate", str(job.get("name") or ""))
        or job.get("replica_retry_limit") != 0
        or job.get("replica_timeout_in_seconds") != 1800
        or job_container.get("image") != BASELINE_JOB_IMAGE
        or job_container.get("command") != ["/bootstrap-disabled"]
        or job_container.get("args") not in (None, [])
        or job_container.get("env") not in (None, [])
        or job_template.get("init_container") not in (None, [])
        or job.get("schedule_trigger_config") not in (None, [])
        or job.get("event_trigger_config") not in (None, [])
        or job.get("manual_trigger_config") != [{"parallelism": 1, "replica_completion_count": 1}]
        or job.get("secret") not in (None, [])
        or job.get("registry") not in (None, [])
        or len(job.get("identity") or []) != 1
        or job["identity"][0].get("type") != "UserAssigned"
    ):
        raise ValueError("Baseline migration job must remain the exact inert manual placeholder.")
    migration_identity_id = _value(resources["module.migration_identity.azurerm_user_assigned_identity.this"]).get("id")
    job_identity_ids = (job.get("identity") or [{}])[0].get("identity_ids") or []
    if migration_identity_id and job_identity_ids and job_identity_ids != [migration_identity_id]:
        raise ValueError("Baseline migration job identity is not bound to the migration identity.")

    region_resources = (
        "azapi_resource.sql_database",
        "azurerm_container_app_job.migration",
        "module.container_app.azapi_resource.container_app",
        "module.container_app_environment.azapi_resource.this",
        "module.identity.azurerm_user_assigned_identity.this",
        "module.key_vault.azapi_resource.this",
        "module.log_analytics.azurerm_log_analytics_workspace.this",
        "module.migration_identity.azurerm_user_assigned_identity.this",
        "module.sql_server.azurerm_mssql_server.this",
    )
    if any(_value(resources[address]).get("location") != "switzerlandnorth" for address in region_resources):
        raise ValueError("Baseline resources must remain in Switzerland North.")

    if "identity[0].identity_ids[0]" in job_unknown:
        _require_dependency(relevant, "module.migration_identity.azurerm_user_assigned_identity.this", ("id",))

    for output_name, identity_address in (
        ("runtime_sql_connection_string", "module.identity.azurerm_user_assigned_identity.this"),
        ("migration_sql_connection_string", "module.migration_identity.azurerm_user_assigned_identity.this"),
    ):
        output = outputs.get(output_name)
        if not isinstance(output, dict) or output.get("after_sensitive") is not True:
            raise ValueError("Baseline SQL connection outputs must remain sensitive.")
        if has_unknown(output.get("after_unknown")):
            _require_dependency(relevant, identity_address, ("client_id",))
        else:
            client_id = _value(resources[identity_address]).get("client_id")
            server_name = _value(resources["module.sql_server.azurerm_mssql_server.this"]).get("name")
            expected_connection = (
                f"Server=tcp:{server_name}.database.windows.net,1433;Initial Catalog=bujo;Encrypt=True;"
                f"TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id={client_id}"
            )
            if output.get("after") != expected_connection:
                raise ValueError("Baseline SQL connection output is not bound to the expected identity and database.")

    return result


def validate_baseline_plan(plan: dict, *, expected_image: str, expected_owner: str, expected_app_id: str) -> list[tuple[str, str]]:
    """Validate the reviewed full-root infrastructure baseline without loosening the legacy job-only path."""
    if plan.get("errored") is True or plan.get("deferred_changes") or plan.get("complete") is False:
        raise ValueError("Baseline plan is errored or contains deferred changes.")
    return _baseline_configuration(plan, expected_image, expected_owner, expected_app_id)


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
