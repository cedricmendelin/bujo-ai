using System.Text.Json;
using Bujo.Domain;
using Bujo.Web.Components;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Bujo.E2E.Tests;

public sealed class JournalPresentationTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>
{
    [Theory]
    [InlineData(320)]
    [InlineData(390)]
    [InlineData(1280)]
    public async Task LongJournalContentWrapsAndVisibleControlsRemainTouchSized(int width)
    {
        var (list, occurrence) = SeedLongContent(width);
        await using var context = await fixture.CreateContextAsync(width: width);
        var page = await context.NewPageAsync();
        foreach (var path in new[] { "/", "/today", "/upcoming", "/capture", "/lists", $"/lists/{list.Id}", $"/lists/{list.Id}/review", $"/items/{occurrence.Id}", "/rendering-proof" })
        {
            await page.GotoAsync(path);
            if (path == "/")
            {
                await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Chat", Level = 1 })).ToBeVisibleAsync();
                await Expect(page).ToHaveTitleAsync("Chat · Bujo-AI");
            }
            else if (path != "/rendering-proof")
                await Expect(page.Locator(".notation-legend")).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("() => window.bujoPwa?.writesAvailable()");
            await AssertLayoutAsync(page, path, width);
        }
    }

    [Fact]
    public async Task KeyboardLegendAndCaptureRemainUsableWithReducedMotionAndForcedColors()
    {
        await using var context = await fixture.CreateContextAsync(width: 390);
        var page = await context.NewPageAsync();
        await page.EmulateMediaAsync(new() { ForcedColors = ForcedColors.Active, ReducedMotion = ReducedMotion.Reduce });
        await page.GotoAsync("/capture");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Capture item", Exact = true })).ToBeEnabledAsync();
        await Expect(page.GetByLabel("Item name", new() { Exact = true })).ToBeEditableAsync();
        var summary = page.Locator(".notation-legend summary");
        await summary.FocusAsync();
        await Expect(summary).ToBeFocusedAsync();
        Assert.NotEqual("none", await summary.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator(".notation-legend")).ToHaveAttributeAsync("open", "");
        await Expect(page.Locator(".notation-legend")).ToContainTextAsync("Item is the umbrella term for a task or a thought");
        await Expect(page.Locator(".notation-legend .journal-marker[data-state=open]")).ToHaveCountAsync(2);
        var abandoned = page.Locator(".notation-legend .journal-marker[data-state=abandoned]");
        await Expect(abandoned.Locator(".journal-marker-label")).ToHaveTextAsync("Item, abandoned");
        Assert.Contains("line-through", await abandoned.Locator(".journal-glyph")
            .EvaluateAsync<string>("element => getComputedStyle(element).textDecorationLine"));
        await page.Keyboard.PressAsync("Enter");
        var input = page.GetByLabel("Item name", new() { Exact = true });
        for (var index = 0; index < 15 && !await input.EvaluateAsync<bool>("node => node === document.activeElement"); index++)
            await page.Keyboard.PressAsync("Tab");
        await Expect(input).ToBeFocusedAsync();
        Assert.NotEqual("none", await input.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
        Assert.Equal("0s", await input.EvaluateAsync<string>("element => getComputedStyle(element).transitionDuration"));
        Assert.Equal("none", await input.EvaluateAsync<string>("element => getComputedStyle(element).animationName"));
        await page.Keyboard.TypeAsync("Synthetic keyboard capture");
        await page.GetByRole(AriaRole.Button, new() { Name = "Capture item", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var confirmation = page.Locator(".journal-confirmation[role=status]");
        await Expect(confirmation).ToHaveTextAsync("Item captured.");
        Assert.Equal("solid", await confirmation.EvaluateAsync<string>("element => getComputedStyle(element).borderInlineStartStyle"));
        Assert.True(await confirmation.EvaluateAsync<bool>("element => parseFloat(getComputedStyle(element).borderInlineStartWidth) >= 3"));
        Assert.True(await page.EvaluateAsync<bool>("matchMedia('(forced-colors: active)').matches && matchMedia('(prefers-reduced-motion: reduce)').matches"));
        Assert.Empty((await page.RunAxe()).Violations);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(390)]
    [InlineData(1280)]
    public async Task AppOwnedAccountPagesUseTheSharedStylesheetAndAccessibleCard(int width)
    {
        await using var context = await fixture.CreateContextAsync(identity: null, width: width);
        var page = await context.NewPageAsync();
        var externalRequests = new List<string>();
        await context.RouteAsync("https://**/*", route => { externalRequests.Add(route.Request.Url); return route.AbortAsync(); });
        foreach (var path in new[] { "/", "/auth/sign-in", "/auth/signed-out", "/auth/denied", "/auth/error" })
        {
            await page.GotoAsync(path);
            await Expect(page.Locator("link[rel=stylesheet][href='/_content/Bujo.Web/pwa.css']")).ToHaveCountAsync(1);
            await Expect(page.Locator(path == "/" ? "section.auth-card" : "main.auth-card")).ToBeVisibleAsync();
            await AssertLayoutAsync(page, path, width);
            if (path == "/auth/sign-in")
            {
                var signIn = page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true });
                for (var index = 0; index < 10 && !await signIn.EvaluateAsync<bool>("element => element === document.activeElement"); index++)
                    await page.Keyboard.PressAsync("Tab");
                await Expect(signIn).ToBeFocusedAsync();
                Assert.NotEqual("none", await signIn.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
            }
        }
        Assert.Empty(externalRequests);
        await using var approved = await fixture.CreateContextAsync(width: width);
        var signOut = await approved.NewPageAsync();
        await signOut.GotoAsync("/auth/sign-out");
        await Expect(signOut.Locator("main.auth-card")).ToBeVisibleAsync();
        await AssertLayoutAsync(signOut, "/auth/sign-out", width);
    }

    private static async Task AssertLayoutAsync(IPage page, string path, int width)
    {
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"),
            $"{path} overflows at {width}px.");
        var undersized = await page.Locator("a, button, input:not([type=hidden]), select, textarea, summary")
            .EvaluateAllAsync<string[]>("""
                elements => elements.filter(element => {
                    const target = element.matches('input[type=checkbox], input[type=radio]')
                        && element.labels?.length ? element.labels[0] : element;
                    const box = target.getBoundingClientRect();
                    return box.width && box.height && getComputedStyle(element).visibility !== 'hidden'
                        && (box.width < 44 || box.height < 44);
                }).map(element => `${element.tagName}: ${element.textContent.trim()} (${element.getBoundingClientRect().width}x${element.getBoundingClientRect().height})`)
                """);
        Assert.True(undersized.Length == 0, $"{path} at {width}px: {string.Join("; ", undersized)}");
        var axe = await page.RunAxe();
        Assert.True(axe.Violations.Length == 0, $"{path}: {JsonSerializer.Serialize(axe.Violations)}");
    }

    private (JournalListSnapshot, OccurrenceSnapshot) SeedLongContent(int width)
    {
        var listResult = fixture.Journal.CreateList(new($"Synthetic {width} " + new string('L', 80), JournalListKind.Custom));
        Assert.True(listResult.IsSuccess, listResult.Error?.Message);
        var list = listResult.Value!;
        var result = fixture.Journal.Capture(new(new string('T', 200), new string('D', 300),
            [new string('t', 64), new string('u', 64)], PlannedPeriod.ForDay(new DateOnly(2026, 11, 19)),
            list.Id, list.Version, DateTimeOffset.UtcNow));
        Assert.True(result.IsSuccess, result.Error?.Message);
        return (list, result.Value!.Occurrence);
    }
}

public sealed class JournalMarkerRenderingTests
{
    public static IEnumerable<object[]> ConventionPreviews() =>
        from type in new[] { "Task", "Thought" }
        from state in Enum.GetValues<OccurrenceState>()
        select new object[] { type, state };

    [Theory]
    [MemberData(nameof(ConventionPreviews))]
    public async Task ConventionPreviewsExposeEveryTypeAndStateWithoutReadingTheGlyph(string type, OccurrenceState state)
    {
        var html = await RenderAsync(type, state);
        Assert.Contains($"data-state=\"{state.ToString().ToLowerInvariant()}\"", html);
        var status = type == "Thought" && state == OccurrenceState.Open ? "reflection" : state.ToString().ToLowerInvariant();
        Assert.Contains($"<span class=\"journal-marker-label\">{type}, {status}</span>", html);
        Assert.Contains("class=\"journal-glyph\" aria-hidden=\"true\"", html);
        var glyph = state switch { OccurrenceState.Done => "x", OccurrenceState.Moved => ">", _ => type == "Task" ? "•" : "−" };
        Assert.Contains($">{glyph}</span>", html);
    }

    public sealed class JournalFeedbackRenderingTests
    {
        [Theory]
        [InlineData(false, false, "Loading the journal")]
        [InlineData(true, true, "Working")]
        public async Task UnconfirmedWorkIsAnnouncedWithoutClaimingASave(bool ready, bool busy, string message)
        {
            var html = await RenderAsync(new() { [nameof(JournalFeedback.Ready)] = ready, [nameof(JournalFeedback.Busy)] = busy });
            Assert.Contains("role=\"status\"", html);
            Assert.Contains(message, html);
            Assert.DoesNotContain("Item captured", html);
            Assert.DoesNotContain("saved", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RejectedWritesKeepTheRealErrorAndRefreshActionWithoutSuccess()
        {
            var html = await RenderAsync(new()
            {
                [nameof(JournalFeedback.Ready)] = true,
                [nameof(JournalFeedback.Error)] = "Save not confirmed. Refresh the journal."
            });
            Assert.Contains("role=\"alert\">Save not confirmed. Refresh the journal.", html);
            Assert.Contains(">Refresh journal</button>", html);
            Assert.DoesNotContain("Item captured", html);
        }

        private static async Task<string> RenderAsync(Dictionary<string, object?> parameters)
        {
            await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<JournalFeedback>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        }
    }

    [Fact]
    public async Task UntypedStoredItemsDoNotClaimTaskOrThoughtClassification()
    {
        var html = await RenderAsync(null, OccurrenceState.Open);
        Assert.Contains(">Item, open</span>", html);
        Assert.Contains(">○</span>", html);
        Assert.DoesNotContain("Task", html);
        Assert.DoesNotContain("Thought", html);
    }

    private static async Task<string> RenderAsync(string? type, OccurrenceState state)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<JournalMarker>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(JournalMarker.Type)] = type, [nameof(JournalMarker.State)] = state }));
            return System.Net.WebUtility.HtmlDecode(result.ToHtmlString());
        });
    }
}
