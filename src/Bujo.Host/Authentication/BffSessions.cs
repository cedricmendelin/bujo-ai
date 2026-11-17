using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Bujo.Host.Authentication;

public interface IBffSessionValidator
{
    Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

// Single-replica, memory-only tokens. Restart requires sign-in; tokens never enter the cookie.
public sealed class BffSessions(IOptionsMonitor<JwtBearerOptions> bearerOptions) : IBffSessionValidator, IDisposable
{
    public const string SessionClaim = "bujo:session";
    private readonly MemoryCache _sessions = new(new MemoryCacheOptions { SizeLimit = 1024 });

    public async Task<ClaimsPrincipal?> ValidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var options = bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
            var parameters = options.TokenValidationParameters.Clone();
            if (options.ConfigurationManager is not null)
            {
                var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
                parameters.ValidIssuer = configuration.Issuer;
                parameters.IssuerSigningKeys = configuration.SigningKeys;
            }

            var result = await new JsonWebTokenHandler { MapInboundClaims = false }
                .ValidateTokenAsync(token, parameters);
            return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed without logging tokens, identifiers, or upstream error descriptions.
            return null;
        }
    }

    public string Create(string token, ClaimsPrincipal principal)
    {
        var id = Guid.NewGuid().ToString("N");
        var expires = new DateTimeOffset(new JsonWebToken(token).ValidTo, TimeSpan.Zero);
        _sessions.Set(id, new Session(token, principal.FindFirstValue("tid"), principal.FindFirstValue("oid")),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expires < DateTimeOffset.UtcNow.AddHours(1) ? expires : DateTimeOffset.UtcNow.AddHours(1),
                Size = 1
            });
        return id;
    }

    public void Remove(ClaimsPrincipal principal)
    {
        if (principal.FindFirstValue(SessionClaim) is { } id)
            _sessions.Remove(id);
    }

    public async Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            principal.FindFirstValue(SessionClaim) is not { } id ||
            !_sessions.TryGetValue<Session>(id, out var session) || session is null)
            return false;

        var tokenPrincipal = await ValidateTokenAsync(session.Token, cancellationToken);
        var valid = tokenPrincipal is not null &&
            SameIdentity(principal, tokenPrincipal) &&
            session.TenantId == tokenPrincipal.FindFirstValue("tid") &&
            session.ObjectId == tokenPrincipal.FindFirstValue("oid") &&
            tokenPrincipal.FindFirstValue("scp")?.Split(' ').Contains("journal.access", StringComparer.Ordinal) == true;
        if (!valid)
            _sessions.Remove(id);
        return valid;
    }

    public static bool SameIdentity(ClaimsPrincipal left, ClaimsPrincipal right) =>
        left.FindFirstValue("tid") is { } tenant &&
        left.FindFirstValue("oid") is { } subject &&
        StringComparer.OrdinalIgnoreCase.Equals(tenant, right.FindFirstValue("tid")) &&
        StringComparer.OrdinalIgnoreCase.Equals(subject, right.FindFirstValue("oid"));

    private sealed record Session(string Token, string? TenantId, string? ObjectId);

    public void Dispose() => _sessions.Dispose();
}
