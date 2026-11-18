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

    public string? TenantId =>
        Guid.TryParse(_configuration["Auth:TenantId"], out var tenantId) && tenantId != Guid.Empty
            ? tenantId.ToString("D")
            : null;

    public IReadOnlySet<string> AllowedObjectIds => ParseAllowedObjectIds().ObjectIds;

    public int InvalidAllowedPrincipalEntryCount => ParseAllowedObjectIds().InvalidEntryCount;

    private (IReadOnlySet<string> ObjectIds, int InvalidEntryCount) ParseAllowedObjectIds()
    {
        var objectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidEntryCount = 0;
        var configuredEntries = (_configuration["Auth:AllowedPrincipals"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in configuredEntries)
        {
            if (Guid.TryParse(entry, out var objectId) && objectId != Guid.Empty)
            {
                objectIds.Add(objectId.ToString("D"));
            }
            else
            {
                invalidEntryCount++;
            }
        }

        return (objectIds, invalidEntryCount);
    }
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
