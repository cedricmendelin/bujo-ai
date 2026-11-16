using System.Security.Claims;
using Bujo.Host;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;

namespace Bujo.Integration.Tests;

public sealed class ApprovedPrincipalAuthorizationTests
{
    [Fact]
    public async Task ApprovedPrincipalWithScopeIsAuthorized()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            ("Auth:AllowedPrincipals", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7:2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72")));
        var context = CreateContext(requirement,
            ("tid", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            ("oid", "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72"),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task AuthenticatedPrincipalOutsideAllowlistIsDenied()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            ("Auth:AllowedPrincipals", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7:2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72")));
        var context = CreateContext(requirement,
            ("tid", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            ("oid", "f7a16bf2-f207-428a-83e4-d398d01a4276"),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task MissingAllowlistDeniesDataAccess()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7")));
        var context = CreateContext(requirement,
            ("tid", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            ("oid", "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72"),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    private static AuthorizationHandlerContext CreateContext(
        IAuthorizationRequirement requirement,
        params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), "Bearer");
        return new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), null);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value))
            .Build();
}
