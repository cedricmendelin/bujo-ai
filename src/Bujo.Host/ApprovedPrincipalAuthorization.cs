using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Bujo.Host;

public sealed class ApprovedPrincipalRequirement : IAuthorizationRequirement
{
    private readonly IConfiguration _configuration;

    public ApprovedPrincipalRequirement(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? TenantId => Guid.TryParse(_configuration["Auth:TenantId"], out var tenantId)
        ? tenantId.ToString("D") : null;

    public IReadOnlySet<string> AllowedObjectIds =>
        (_configuration["Auth:AllowedPrincipals"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts =>
                parts.Length == 2 &&
                Guid.TryParse(parts[0], out var configuredTenantId) &&
                Guid.TryParse(parts[1], out _) &&
                TenantId is not null &&
                StringComparer.OrdinalIgnoreCase.Equals(configuredTenantId.ToString("D"), TenantId))
            .Select(parts => Guid.Parse(parts[1]).ToString("D"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class ApprovedPrincipalHandler : AuthorizationHandler<ApprovedPrincipalRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ApprovedPrincipalRequirement requirement)
    {
        var tenantId = context.User.FindFirstValue("tid");
        var objectId = context.User.FindFirstValue("oid");
        var scopes = context.User.FindFirstValue("scp")?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (requirement.TenantId is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(tenantId, requirement.TenantId) &&
            Guid.TryParse(objectId, out var parsedObjectId) &&
            requirement.AllowedObjectIds.Contains(parsedObjectId.ToString("D")) &&
            scopes?.Contains("journal.access", StringComparer.Ordinal) == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
