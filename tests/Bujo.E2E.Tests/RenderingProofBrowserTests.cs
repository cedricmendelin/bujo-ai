using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Bujo.E2E.Tests;

public sealed class RenderingProofBrowserTests(BrowserFixture fixture, ITestOutputHelper output) : IClassFixture<BrowserFixture>
{
    [Theory]
    [InlineData(1280, 900)]
    [InlineData(390, 844)]
    public async Task ApprovedAccountCanUseAnAccessibleKeyboardOperatedServerCircuit(int width, int height)
    {
        await using var context = await fixture.CreateContextAsync(width: width, height: height);
        var page = await context.NewPageAsync();
        var browserErrors = new List<string>();
        page.PageError += (_, error) => { browserErrors.Add(error); output.WriteLine(error); };
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                browserErrors.Add(message.Text);
                output.WriteLine(message.Text);
            }
        };
        var apiWrites = new List<string>();
        page.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) &&
                request.Method is not "GET" and not "HEAD")
                apiWrites.Add($"{request.Method} {request.Url}");
        };

        var response = await page.GotoAsync("/rendering-proof");
        Assert.Equal(200, response!.Status);
        var input = page.Locator("#proof-input");
        var action = page.Locator("#proof-action");
        var result = page.Locator("#proof-result");
        await Expect(action).ToBeEnabledAsync();
        await Expect(input).ToHaveAccessibleNameAsync(new System.Text.RegularExpressions.Regex(".+"));
        await Expect(action).ToHaveAccessibleNameAsync(new System.Text.RegularExpressions.Regex(".+"));

        var axe = await page.RunAxe();
        Assert.True(axe.Violations.Length == 0, JsonSerializer.Serialize(axe.Violations));
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
            "The proof must not overflow a mobile viewport.");

        for (var index = 0; index < 15 && !await input.EvaluateAsync<bool>("element => element === document.activeElement"); index++)
            await page.Keyboard.PressAsync("Tab");
        await Expect(input).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("Synthetic unsaved proof");
        await page.Keyboard.PressAsync("Tab");
        await Expect(action).ToBeFocusedAsync();
        await Expect(result).ToHaveTextAsync("No server roundtrip requested.");
        await page.Keyboard.PressAsync("Enter");
        await Expect(result).ToHaveTextAsync("Connection confirmed. Check 1. Nothing was saved.");
        await Expect(input).ToHaveValueAsync("Synthetic unsaved proof");
        Assert.Empty(apiWrites);
        Assert.Empty(browserErrors);
    }

    [Fact]
    public async Task ManifestAndActualServiceWorkerCacheOnlyThePublicStaticAllowlist()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/rendering-proof");
        await page.EvaluateAsync("async () => { await navigator.serviceWorker.ready; }");
        await page.ReloadAsync();
        await page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null");
        var manifestHref = await page.Locator("link[rel=manifest]").GetAttributeAsync("href");
        Assert.Equal("/_content/Bujo.Web/manifest.webmanifest", manifestHref);
        var manifestResponse = await context.APIRequest.GetAsync(manifestHref!);
        Assert.True(manifestResponse.Ok);
        using var manifest = JsonDocument.Parse(await manifestResponse.TextAsync());
        var root = manifest.RootElement;
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.StartsWith("/", root.GetProperty("start_url").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        foreach (var size in new[] { 192, 512 })
        {
            var icon = Assert.Single(root.GetProperty("icons").EnumerateArray(),
                icon => icon.GetProperty("sizes").GetString() == $"{size}x{size}");
            var source = icon.GetProperty("src").GetString()!;
            Assert.Equal($"/_content/Bujo.Web/icons/icon-{size}.png", source);
            Assert.Equal("image/png", icon.GetProperty("type").GetString());
            var dimensions = await page.EvaluateAsync<int[]>(
                "src => new Promise((resolve, reject) => { const image = new Image(); image.onload = () => resolve([image.naturalWidth, image.naturalHeight]); image.onerror = reject; image.src = src; })",
                source);
            Assert.Equal(new[] { size, size }, dimensions);
        }

        await page.EvaluateAsync("""
            async () => {
                for (const path of ["/api/journal", "/auth/sign-in", "/auth/signed-out", "/auth/denied",
                    "/auth/error", "/rendering-proof", "/_content/Bujo.Web/pwa.css?private=1"]) {
                    await fetch(path);
                }
            }
            """);
        await page.ReloadAsync();
        var entries = await page.EvaluateAsync<JsonElement>("""
            async () => {
                const entries = [];
                for (const name of await caches.keys()) {
                    const cache = await caches.open(name);
                    for (const request of await cache.keys()) {
                        const response = await cache.match(request);
                        entries.push({ path: new URL(request.url).pathname + new URL(request.url).search,
                            type: response.headers.get("content-type") });
                    }
                }
                return entries;
            }
            """);
        var expected = new[]
        {
            "/_content/Bujo.Web/pwa.css",
            "/_content/Bujo.Web/pwa.js",
            "/_content/Bujo.Web/manifest.webmanifest",
            "/_content/Bujo.Web/icons/icon-192.png",
            "/_content/Bujo.Web/icons/icon-512.png"
        };
        Assert.Equal(expected.Order(), entries.EnumerateArray()
            .Select(entry => entry.GetProperty("path").GetString()).Order());
        Assert.All(entries.EnumerateArray(), entry =>
            Assert.DoesNotContain("html", entry.GetProperty("type").GetString()!, StringComparison.OrdinalIgnoreCase));
        var devTools = await context.NewCDPSessionAsync(page);
        var installability = await devTools.SendAsync("Page.getInstallabilityErrors");
        Assert.NotNull(installability);
        Assert.Empty(installability.Value.GetProperty("installabilityErrors").EnumerateArray());
    }

    [Theory]
    [InlineData(null, "/auth/sign-in")]
    [InlineData("deny", "/auth/denied")]
    public async Task AnonymousAndUnapprovedAccountsCannotSeeTheProof(string? identity, string expectedPath)
    {
        await using var context = await fixture.CreateContextAsync(identity);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/rendering-proof");
        Assert.Equal(expectedPath, new Uri(page.Url).AbsolutePath);
        await Expect(page.Locator("#proof-input")).ToHaveCountAsync(0);
        await Expect(page.Locator("#proof-result")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task UiCookieDoesNotAuthorizeTheBearerApi()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/rendering-proof");
        await Expect(page.Locator("#proof-action")).ToBeEnabledAsync();
        var response = await context.APIRequest.GetAsync("/api/journal");
        Assert.Equal(401, response.Status);
        Assert.Contains("Bearer", response.Headers["www-authenticate"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineAndReconnectRetainUnsavedInputWithoutReplayingAnAction()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/rendering-proof");
        var input = page.Locator("#proof-input");
        var action = page.Locator("#proof-action");
        var result = page.Locator("#proof-result");
        await page.WaitForFunctionAsync("() => window.bujoPwa?.writesAvailable() === true");
        await Expect(action).ToBeEnabledAsync();
        await input.FillAsync("First explicit circuit action");
        await Expect(result).ToHaveTextAsync("No server roundtrip requested.");
        await action.ClickAsync();
        await Expect(result).ToHaveTextAsync("Connection confirmed. Check 1. Nothing was saved.");
        var acknowledged = await result.InnerTextAsync();
        await input.FillAsync("Retained unsaved input");

        await context.SetOfflineAsync(true);
        try
        {
            await Expect(page.Locator("[data-connectivity-status]")).ToContainTextAsync("Offline");
            await Expect(action).ToBeDisabledAsync();
            await Expect(input).ToHaveValueAsync("Retained unsaved input");
            await Expect(result).ToHaveTextAsync(acknowledged);
            Assert.False(await page.EvaluateAsync<bool>("window.bujoPwa.writesAvailable()"));
            await action.EvaluateAsync("element => element.click()");
            await Expect(result).ToHaveTextAsync(acknowledged);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        await page.WaitForFunctionAsync("() => window.bujoPwa.writesAvailable() === true", null,
            new() { Timeout = 60000 });
        await Expect(action).ToBeEnabledAsync();
        await Expect(input).ToHaveValueAsync("Retained unsaved input");
        await Expect(result).ToHaveTextAsync(acknowledged);
        await action.ClickAsync();
        await Expect(result).ToHaveTextAsync("Connection confirmed. Check 2. Nothing was saved.");
    }

    [Fact]
    public async Task LostServerTransportDisablesActionsEvenWhenBrowserReportsOnline()
    {
        await using var context = await fixture.CreateContextAsync();
        IWebSocketRoute? activeSocket = null;
        var blockReconnect = false;
        await context.RouteWebSocketAsync(new System.Text.RegularExpressions.Regex(@"/_blazor\?"), socket =>
        {
            if (blockReconnect) return;
            socket.ConnectToServer();
            activeSocket = socket;
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/rendering-proof");
        var action = page.Locator("#proof-action");
        var input = page.Locator("#proof-input");
        var result = page.Locator("#proof-result");
        await Expect(action).ToBeEnabledAsync();
        await input.FillAsync("Unsaved across transport loss");
        var before = await result.InnerTextAsync();
        Assert.NotNull(activeSocket);
        blockReconnect = true;
        await activeSocket.CloseAsync(new() { Code = 1011, Reason = "Synthetic server transport failure" });

        await Expect(page.Locator("[data-circuit-status]")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(page.Locator("[data-circuit-status]")).ToContainTextAsync("Server connection lost");
        Assert.True(await page.EvaluateAsync<bool>("navigator.onLine"));
        await Expect(action).ToBeDisabledAsync();
        await Expect(input).ToHaveValueAsync("Unsaved across transport loss");
        await Expect(result).ToHaveTextAsync(before);
    }

    [Theory]
    [InlineData("/auth/sign-in")]
    [InlineData("/auth/signed-out")]
    [InlineData("/auth/denied")]
    [InlineData("/auth/error")]
    public async Task AnonymousAccountPagesAreSafeAccessibleAndContainNoJournal(string path)
    {
        await using var context = await fixture.CreateContextAsync(identity: null);
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);
        Assert.Equal(path, new Uri(page.Url).AbsolutePath);
        await Expect(page.Locator("h1")).ToBeVisibleAsync();
        await Expect(page.Locator("#proof-input, #proof-result, [data-journal-write]")).ToHaveCountAsync(0);
        Assert.Empty((await page.RunAxe()).Violations);
        var unsafeSignIn = await context.APIRequest.PostAsync("/auth/sign-in");
        Assert.Equal(400, unsafeSignIn.Status);
    }

}
