using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Bujo.Host.Authentication;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/sign-in", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var token = antiforgery.GetAndStoreTokens(context);
            return Page("Sign in",
                $"""
                <p>Use your approved account to continue. No journal data is shown here.</p>
                <form method="post" action="/auth/sign-in">
                  <input type="hidden" name="{HtmlEncoder.Default.Encode(token.FormFieldName)}" value="{HtmlEncoder.Default.Encode(token.RequestToken!)}">
                  <button type="submit">Sign in</button>
                </form>
                """);
        }).AllowAnonymous();

        app.MapPost("/auth/sign-in", async (HttpContext context, IAntiforgery antiforgery, IConfiguration configuration) =>
        {
            if (!await ValidAntiforgeryAsync(context, antiforgery))
                return Results.BadRequest();
            if (!Guid.TryParse(configuration["Auth:TenantId"], out _) ||
                !Guid.TryParse(configuration["Auth:Pwa:ClientId"], out _) ||
                !BffAuthentication.HasClientCredential(configuration) ||
                string.IsNullOrWhiteSpace(configuration["Auth:Pwa:Scope"]))
                return Results.Redirect("/auth/error");
            return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" },
                [BffAuthentication.OidcScheme]);
        }).AllowAnonymous();

        app.MapGet("/auth/sign-out", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var token = antiforgery.GetAndStoreTokens(context);
            return Page("Sign out", $"""
                <p>Sign out of this application on this browser?</p>
                <form method="post" action="/auth/sign-out">
                  <input type="hidden" name="{HtmlEncoder.Default.Encode(token.FormFieldName)}" value="{HtmlEncoder.Default.Encode(token.RequestToken!)}">
                  <button type="submit">Sign out</button>
                </form>
                """);
        }).RequireAuthorization(BffAuthentication.UiPolicy);

        app.MapPost("/auth/sign-out", async (
            HttpContext context, IAntiforgery antiforgery, BffSessions sessions) =>
        {
            if (!await ValidAntiforgeryAsync(context, antiforgery))
                return Results.BadRequest();
            sessions.Remove(context.User);
            await context.SignOutAsync(BffAuthentication.CookieScheme);
            // Local revocation is immediate. Do not retain an ID token just for upstream logout.
            return Results.Redirect("/auth/signed-out");
        }).RequireAuthorization(BffAuthentication.UiPolicy);

        app.MapGet("/auth/signed-out", () => Page("Signed out", "<p>You are signed out of this application.</p>")).AllowAnonymous();
        app.MapGet("/auth/denied", () => Page("Access denied", "<p>This account cannot access this application.</p>")).AllowAnonymous();
        app.MapGet("/auth/error", () => Page("Sign-in unavailable", "<p>Sign-in could not be completed. Please try again later.</p>")).AllowAnonymous();
    }

    private static IResult Page(string title, string body) => Results.Content(
        $"""
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{title} — Bujo-AI</title><link rel="stylesheet" href="/_content/Bujo.Web/pwa.css">
        <link rel="icon" type="image/png" href="/_content/Bujo.Web/icons/icon-192.png">
        </head><body><main><h1>{title}</h1>{body}<p><a href="/">Home</a></p></main></body></html>
        """, "text/html");

    private static async Task<bool> ValidAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
