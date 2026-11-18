using System.Security.Claims;
using Bujo.Host;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;

namespace Bujo.Integration.Tests;

public sealed class ApprovedPrincipalAuthorizationTests
{
    private const string TenantId = "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7";
    private const string ObjectId = "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72";

    [Fact]
    public async Task ApprovedObjectIdWithMatchingTenantAndScopeIsAuthorized()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", TenantId),
            ("Auth:AllowedPrincipals", ObjectId)));
        var context = CreateContext(requirement,
            ("tid", TenantId),
            ("oid", ObjectId),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task ObjectIdIsTrimmedAndComparedAsGuidRegardlessOfCase()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", TenantId.ToUpperInvariant()),
            ("Auth:AllowedPrincipals", $"  {ObjectId.ToUpperInvariant()} , ")));
        var context = CreateContext(requirement,
            ("tid", TenantId.ToUpperInvariant()),
            ("oid", ObjectId.ToLowerInvariant()),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(0, requirement.InvalidAllowedPrincipalEntryCount);
    }

    [Fact]
    public async Task MatchingObjectIdFromAnotherTenantIsDenied()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", TenantId),
            ("Auth:AllowedPrincipals", ObjectId)));
        var context = CreateContext(requirement,
            ("tid", "f7a16bf2-f207-428a-83e4-d398d01a4276"),
            ("oid", ObjectId),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task AuthenticatedPrincipalOutsideAllowlistIsDenied()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", TenantId),
            ("Auth:AllowedPrincipals", ObjectId)));
        var context = CreateContext(requirement,
            ("tid", TenantId),
            ("oid", "f7a16bf2-f207-428a-83e4-d398d01a4276"),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task LegacyTenantObjectPairIsRejectedAndReportedAsInvalid()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", TenantId),
            ("Auth:AllowedPrincipals", $"{TenantId}:{ObjectId}")));
        var context = CreateContext(requirement,
            ("tid", TenantId),
            ("oid", ObjectId),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(1, requirement.InvalidAllowedPrincipalEntryCount);
        Assert.Empty(requirement.AllowedObjectIds);
    }

    [Fact]
    public async Task InvalidEntryDoesNotAuthorizeItsObjectId()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", TenantId),
            ("Auth:AllowedPrincipals", $"invalid,{TenantId}:{ObjectId}")));
        var context = CreateContext(requirement,
            ("tid", TenantId),
            ("oid", ObjectId),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(2, requirement.InvalidAllowedPrincipalEntryCount);
    }

    [Fact]
    public async Task MissingAllowlistDeniesDataAccess()
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", TenantId)));
        var context = CreateContext(requirement,
            ("tid", TenantId),
            ("oid", ObjectId),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-tenant")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrInvalidTenantDeniesAllPrincipals(string tenantId)
    {
        var requirement = new ApprovedPrincipalRequirement(Configuration(
            ("Auth:TenantId", tenantId),
            ("Auth:AllowedPrincipals", ObjectId)));
        var context = CreateContext(requirement,
            ("tid", TenantId),
            ("oid", ObjectId),
            ("scp", "journal.access"));

        await new ApprovedPrincipalHandler().HandleAsync(context);

        Assert.Null(requirement.TenantId);
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
