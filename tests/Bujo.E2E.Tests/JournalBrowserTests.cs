using System.Text.Json;
using Bujo.Domain;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Bujo.E2E.Tests;

public sealed class JournalBrowserTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>
{
    [Fact]
    public async Task OnlineStartupAndEnhancedJournalNavigationDoNotBlockWrites()
    {
        await using var context = await fixture.CreateContextAsync();
        var startCircuit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RouteAsync("**/_blazor/negotiate?*", async route =>
        {
            await startCircuit.Task;
            await route.ContinueAsync();
        });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync("/lists", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Lists", Level = 1 })).ToBeVisibleAsync();
            await AssertOnlineLayoutAsync(page);
        }
        finally
        {
            startCircuit.TrySetResult();
        }

        await Expect(Button(page, "Create list")).ToBeEnabledAsync();
        await page.EvaluateAsync("window.journalDocument = true");
        await Link(page, "Today").ClickAsync();
        await Expect(page.GetByText("Local date:", new() { Exact = false })).ToBeVisibleAsync();
        await AssertOnlineLayoutAsync(page);
        await Link(page, "Lists").ClickAsync();
        await Expect(Button(page, "Create list")).ToBeEnabledAsync();
        await AssertOnlineLayoutAsync(page);
        await Link(page, "Future Log").ClickAsync();
        await Expect(Button(page, "Save goal")).ToBeEnabledAsync();
        await AssertOnlineLayoutAsync(page);
        await page.GetByLabel("Goal (optional)").FillAsync("Online navigation goal");
        await Button(page, "Save goal").ClickAsync();
        await Expect(page.GetByText("Saved goal: Online navigation goal")).ToBeVisibleAsync();
        await Link(page, "Capture").ClickAsync();
        await Expect(Button(page, "Capture item")).ToBeEnabledAsync();
        await AssertOnlineLayoutAsync(page);
        Assert.True(await page.EvaluateAsync<bool>("window.journalDocument === true"));
    }

    [Fact]
    public async Task KnowledgeCaptureSearchAndReadPreserveSavedWordingAcrossNavigationAndReload()
    {
        await using var context = await fixture.CreateContextAsync(width: 390, height: 844);
        var page = await context.NewPageAsync();
        const string content = "“Keep the original wording.”\r\nCafé <b>is saved text</b>.";

        await page.GotoAsync("/knowledge/new");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Capture knowledge" })).ToBeVisibleAsync();
        var axe = await page.RunAxe();
        Assert.True(axe.Violations.Length == 0, JsonSerializer.Serialize(axe.Violations));
        await page.GetByLabel("Content").FillAsync(content);
        await page.GetByLabel("Title (optional)").FillAsync("Verbatim capture");
        await page.GetByLabel("Source reference (optional)").FillAsync("https://example.invalid/original");
        await page.GetByLabel("Tags (comma-separated, optional)").FillAsync("Quotes, Research");
        await Button(page, "Save entry").ClickAsync();
        await Expect(page.GetByText("Knowledge entry saved.", new() { Exact = true })).ToBeVisibleAsync();
        await Link(page, "View saved entry").ClickAsync();
        await Expect(page.GetByText(content, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("Source reference (not fetched): https://example.invalid/original", new()
        {
            Exact = true
        })).ToBeVisibleAsync();

        await page.ReloadAsync();
        await Expect(page.GetByText(content, new() { Exact = true })).ToBeVisibleAsync();
        await Link(page, "Search knowledge").ClickAsync();
        await page.GetByLabel("Words or a quoted phrase").FillAsync("\"original wording\"");
        await Button(page, "Search").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Verbatim capture" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Verbatim capture" }).ClickAsync();
        await Expect(page.GetByText(content, new() { Exact = true })).ToBeVisibleAsync();

        await page.GotoAsync("/knowledge");
        var downloadTask = page.WaitForDownloadAsync();
        await Button(page, "Export knowledge backup").ClickAsync();
        var download = await downloadTask;
        await using var backupStream = await download.CreateReadStreamAsync();
        using var backupContent = new MemoryStream();
        await backupStream.CopyToAsync(backupContent);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await page.Locator("#knowledge-backup-file").SetInputFilesAsync(new FilePayload
        {
            Name = "knowledge.json",
            MimeType = "application/json",
            Buffer = backupContent.ToArray()
        });
        await Expect(page.GetByText("Knowledge backup restored.", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(null, "/auth/sign-in")]
    [InlineData("deny", "/auth/denied")]
    public async Task UnapprovedSessionsSeeThePublicHomeAndCannotOpenJournalForms(string? identity, string expectedPath)
    {
        await using var context = await fixture.CreateContextAsync(identity);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your bullet journal" })).ToBeVisibleAsync();
        await Expect(Link(page, "Capture")).ToHaveCountAsync(0);
        await page.GotoAsync("/capture");
        await Expect(page).ToHaveURLAsync(fixture.BaseUrl + expectedPath);
        await Expect(page.GetByLabel("Item name")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task JournalJourneyKeepsEarlierListStatesAndResolvesTheLatestAfterTwoMoves()
    {
        await using var context = await fixture.CreateContextAsync(width: 390, height: 844);
        var page = await context.NewPageAsync();
        var selfHttp = new List<string>();
        page.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                selfHttp.Add(request.Url);
        };
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Today", Level = 1 })).ToBeVisibleAsync();
        await Expect(Link(page, "Diagnostics")).ToHaveAttributeAsync("href", "/rendering-proof");
        await Link(page, "Lists").ClickAsync();
        await CreateList(page, "Journey custom");
        await CreateList(page, "Journey weekly", weekly: true);

        await Link(page, "Capture").ClickAsync();
        var name = page.GetByLabel("Item name", new() { Exact = true });
        await Expect(name).ToBeEditableAsync();
        var axe = await page.RunAxe();
        Assert.True(axe.Violations.Length == 0, JsonSerializer.Serialize(axe.Violations));
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        for (var index = 0; index < 25 && !await name.EvaluateAsync<bool>("node => node === document.activeElement"); index++)
            await page.Keyboard.PressAsync("Tab");
        await Expect(name).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("Journal journey");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("Synthetic description");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("work, reading");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByLabel("Destination list")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.GetByLabel("Planned period")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Tab");
        await Expect(Button(page, "Capture item")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.GetByText("Item captured.", new() { Exact = true })).ToBeVisibleAsync();

        await Link(page, "View captured item").ClickAsync();
        await Expect(page.GetByText("Planned period: Unplanned", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("Tags: work, reading", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("Latest: Future Log · Open")).ToBeVisibleAsync();
        var earlierItemUrl = page.Url;
        var earlierListUrl = await page.Locator("p").Filter(new() { HasText = "Latest:" }).GetByRole(AriaRole.Link).GetAttributeAsync("href");
        await page.GetByLabel("Planned period").SelectOptionAsync("Day");
        await page.GetByLabel("Planned date").FillAsync("2026-11-18");
        await Button(page, "Save planned period").ClickAsync();
        await Expect(page.GetByText("Planned period saved.")).ToBeVisibleAsync();
        await Expect(page.GetByText("Latest: Future Log · Open")).ToBeVisibleAsync();
        await MoveAsync(page, "Journey weekly");
        await Expect(page.GetByText("Item moved. Its planned period conflicts with the destination week.")).ToBeVisibleAsync();
        await MoveAsync(page, "Journey custom");
        await Expect(page.GetByText("Latest: Journey custom · Open")).ToBeVisibleAsync();
        await Button(page, "Complete item").ClickAsync();
        await Expect(page.GetByText("Item is now done.")).ToBeVisibleAsync();

        await page.GotoAsync(earlierListUrl!);
        await Expect(page.Locator("li").Filter(new() { HasText = "Journal journey · Moved" })).ToBeVisibleAsync();
        await Link(page, "Journey custom · Done").ClickAsync();
        await Expect(page.GetByText("Latest: Journey custom · Done")).ToBeVisibleAsync();
        await page.GotoAsync(earlierItemUrl);
        await Expect(Link(page, "Open latest occurrence")).ToBeVisibleAsync();
        await Expect(page.Locator("ol li")).ToHaveTextAsync([
            "Future Log · Moved (viewed occurrence)", "Journey weekly · Moved", "Journey custom · Done (latest)"]);
        await page.ReloadAsync();
        await Expect(page.GetByText("Planned period: 2026-11-18")).ToBeVisibleAsync();
        await Expect(page.GetByText("Latest: Journey custom · Done")).ToBeVisibleAsync();
        Assert.Empty(selfHttp);
    }

    [Fact]
    public async Task ListGoalAndLifecycleRespectUnresolvedOccurrencesAndRetainTheDraft()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/lists");
        await CreateList(page, "Lifecycle list");
        await Link(page, "Lifecycle list").ClickAsync();
        var goal = page.GetByRole(AriaRole.Group, new() { Name = "Edit list goal" }).GetByLabel("Goal (optional)");
        await goal.FillAsync("A saved goal");
        await Button(page, "Save goal").ClickAsync();
        await Expect(page.GetByText("Saved goal: A saved goal")).ToBeVisibleAsync();
        await CaptureAsync(page, "Lifecycle item", "Lifecycle list");
        await Link(page, "Lists").ClickAsync();
        await Link(page, "Lifecycle list").ClickAsync();
        await goal.FillAsync("An unsaved goal");
        await Button(page, "Close list").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Resolve all open occurrences");
        await Expect(page.GetByText("List closed.", new() { Exact = true })).ToHaveCountAsync(0);
        await Button(page, "Refresh journal").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await Expect(goal).ToHaveValueAsync("An unsaved goal");
        await Link(page, "Lifecycle item").ClickAsync();
        await Button(page, "Abandon item").ClickAsync();
        await Expect(page.GetByText("Item is now abandoned.")).ToBeVisibleAsync();
        await Link(page, "Lifecycle list").First.ClickAsync();
        await Button(page, "Close list").ClickAsync();
        await Expect(page.GetByText("List closed.", new() { Exact = true })).ToBeVisibleAsync();
        await Link(page, "Lifecycle item").ClickAsync();
        await Expect(page.GetByText("Latest: Lifecycle list · Abandoned")).ToBeVisibleAsync();
        await Expect(Button(page, "Reopen item")).ToHaveCountAsync(0);
        await Link(page, "Lifecycle list").First.ClickAsync();
        await Button(page, "Reopen list").ClickAsync();
        await Expect(page.GetByText("List reopened.", new() { Exact = true })).ToBeVisibleAsync();
        await Link(page, "Lifecycle item").ClickAsync();
        await Button(page, "Reopen item").ClickAsync();
        await Expect(page.GetByText("Latest: Lifecycle list · Open")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ListReviewShowsExplicitDispositionsAndRequiresPwaApprovalBeforeClosure()
    {
        await using var context = await fixture.CreateContextAsync(width: 390, height: 844);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/lists");
        await CreateList(page, "Review workflow");
        await Link(page, "Review workflow").ClickAsync();
        await CaptureAsync(page, "Review disposition", "Review workflow");
        await Link(page, "Lists").ClickAsync();
        await Link(page, "Review workflow").ClickAsync();
        await Link(page, "Start review").ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Unfinished active occurrences" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Review disposition" })).ToBeVisibleAsync();
        var accessibility = await page.RunAxe();
        Assert.Empty(accessibility.Violations);

        await page.GetByLabel("Disposition", new() { Exact = true }).SelectOptionAsync("Done");
        await page.GetByLabel("Close this list after applying all dispositions").CheckAsync();
        await Button(page, "Review proposal").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Proposal · Pending" })).ToBeVisibleAsync();
        Assert.Matches("^[0-9a-f]{64}$", await page.Locator("code").InnerTextAsync());
        await Link(page, "Open this proposal").ClickAsync();
        await page.ReloadAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Proposal · Pending" })).ToBeVisibleAsync();
        await Button(page, "Approve this proposal").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Proposal · Approved" })).ToBeVisibleAsync();
        await Button(page, "Apply approved proposal").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Proposal · Applied" })).ToBeVisibleAsync();
        await Link(page, "Back to list").ClickAsync();
        await Expect(page.GetByText("Closed", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task StaleReviewProposalShowsConflictWithoutClosingOrApplyingChanges()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/lists");
        await CreateList(page, "Stale review workflow");
        await Link(page, "Stale review workflow").ClickAsync();
        await CaptureAsync(page, "Stale review item", "Stale review workflow");
        await Link(page, "Lists").ClickAsync();
        await Link(page, "Stale review workflow").ClickAsync();
        await Link(page, "Start review").ClickAsync();

        await page.GetByLabel("Disposition", new() { Exact = true }).SelectOptionAsync("Done");
        await page.GetByLabel("Close this list after applying all dispositions").CheckAsync();
        await Button(page, "Review proposal").ClickAsync();
        await Button(page, "Approve this proposal").ClickAsync();

        var list = fixture.Journal.GetSnapshot().Lists.Single(candidate => candidate.Name == "Stale review workflow");
        var concurrent = fixture.Journal.Capture(new CaptureCommand(
            "Concurrent review item",
            null,
            null,
            PlannedPeriod.Unplanned,
            list.Id,
            list.Version,
            DateTimeOffset.UtcNow));
        Assert.True(concurrent.IsSuccess, concurrent.Error?.Message);
        await Button(page, "Apply approved proposal").ClickAsync();

        await Expect(page.GetByRole(AriaRole.Alert))
            .ToContainTextAsync("The journal changed since this proposal was reviewed.");
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Unresolved occurrence");
        var snapshot = fixture.Journal.GetSnapshot();
        Assert.Equal(JournalListState.Open, snapshot.Lists.Single(candidate => candidate.Id == list.Id).State);
        Assert.Contains(
            snapshot.Occurrences,
            occurrence => occurrence.ItemId == snapshot.Items.Single(item => item.Name == "Stale review item").Id &&
                          occurrence.State == OccurrenceState.Open);
    }

    [Fact]
    public async Task InvalidIsoWeekShowsDomainErrorWithoutCreatingAList()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/lists");
        await page.GetByLabel("List name").FillAsync("Invalid week test");
        await page.GetByLabel("List type").SelectOptionAsync("Weekly");
        await page.GetByLabel("ISO week-year").FillAsync("2025");
        await page.GetByLabel("ISO week number").FillAsync("53");
        await Button(page, "Create list").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("ISO week must be between 1 and 52 for 2025.");
        await Expect(Link(page, "Invalid week test")).ToHaveCountAsync(0);
        await Expect(page.GetByLabel("List name")).ToHaveValueAsync("Invalid week test");
    }

    [Fact]
    public async Task DuplicateListNameShowsDomainErrorAndWeeklyListsMayShareAWeek()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/lists");
        await CreateList(page, "Repeated name");

        await page.GetByLabel("List name").FillAsync("repeated NAME");
        await Button(page, "Create list").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("A list with this name already exists.");
        await Expect(Link(page, "Repeated name")).ToHaveCountAsync(1);
        await Expect(page.GetByLabel("List name")).ToHaveValueAsync("repeated NAME");

        await page.GetByLabel("List type").SelectOptionAsync("Weekly");
        await page.GetByLabel("List name").FillAsync("Week 48");
        await page.GetByLabel("ISO week-year").FillAsync("2026");
        await page.GetByLabel("ISO week number").FillAsync("48");
        await Button(page, "Create list").ClickAsync();
        await Expect(page.GetByText("List created.", new() { Exact = true })).ToBeVisibleAsync();

        await page.GetByLabel("List type").SelectOptionAsync("Weekly");
        await page.GetByLabel("List name").FillAsync("Planning week 48");
        await page.GetByLabel("ISO week-year").FillAsync("2026");
        await page.GetByLabel("ISO week number").FillAsync("48");
        await Button(page, "Create list").ClickAsync();
        await Expect(page.GetByText("List created.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Link(page, "Planning week 48")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task StaleMovePreservesDestinationAndDraftAndLostAcknowledgementRetriesSameMove()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/lists");
        await CreateList(page, "Conflict first");
        await CreateList(page, "Conflict second");
        await CaptureAsync(page, "Conflict item");
        await Link(page, "View captured item").ClickAsync();
        await Expect(page.GetByText("Latest: Future Log · Open")).ToBeVisibleAsync();
        var other = await context.NewPageAsync();
        await other.GotoAsync(page.Url);
        await MoveAsync(other, "Conflict first");
        await Expect(other.GetByText("Latest: Conflict first · Open")).ToBeVisibleAsync();

        await page.GetByLabel("Item name").FillAsync("Draft after conflict");
        await MoveAsync(page, "Conflict second");
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Refresh the journal");
        await Expect(page.GetByText("Item moved.", new() { Exact = true })).ToHaveCountAsync(0);
        await Button(page, "Refresh journal").ClickAsync();
        await Expect(page.GetByText("Latest: Conflict first · Open")).ToBeVisibleAsync();
        await Expect(page.GetByLabel("Item name")).ToHaveValueAsync("Draft after conflict");
        var destination = page.GetByLabel("Move destination");
        await Expect(destination.Locator("option:checked")).ToHaveTextAsync("Conflict second");

        fixture.Journal.LoseNextMoveReply();
        await Button(page, "Move item").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Save not confirmed");
        await Expect(page.GetByText("Item moved.", new() { Exact = true })).ToHaveCountAsync(0);
        await Button(page, "Refresh journal").ClickAsync();
        await Expect(page.GetByText("Latest: Conflict second · Open")).ToBeVisibleAsync();
        await Button(page, "Retry same move").ClickAsync();
        await Expect(page.GetByText("Item moved.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("ol li")).ToHaveCountAsync(3);
        await Button(page, "Save item details").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Draft after conflict", Level = 1 })).ToBeVisibleAsync();
        var snapshot = fixture.Journal.GetSnapshot();
        var item = Assert.Single(snapshot.Items, item => item.Name == "Draft after conflict");
        Assert.Equal(2, snapshot.MoveHistory.Count(move => move.ItemId == item.Id));
    }

    [Fact]
    public async Task PlanningUsesBrowserZoneAndNeverReschedulesMissedIntentions()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await CaptureAsync(page, "Missed browser day", day: "2026-11-18");
        await CaptureAsync(page, "Upcoming browser day", day: "2026-11-20");
        await Link(page, "Today").ClickAsync();
        await Expect(page.GetByText("Local date: 2026-11-19 (Europe/Zurich)")).ToBeVisibleAsync();
        await Expect(page.GetByText("Planned day 2026-11-18 has passed.", new() { Exact = false })).ToBeVisibleAsync();
        await Link(page, "Missed browser day").ClickAsync();
        await Expect(page.GetByText("Planned period: 2026-11-18")).ToBeVisibleAsync();
        await Link(page, "Upcoming").ClickAsync();
        await Expect(page.GetByText("From 2026-11-19 (Europe/Zurich)", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(Link(page, "Upcoming browser day")).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData("/", "Invalid/Browser", "Unknown or invalid IANA time zone: Invalid/Browser.")]
    [InlineData("/upcoming", "", "An IANA time zone is required.")]
    public async Task InvalidBrowserZoneIsVisibleWithoutUtcFallback(string path, string zone, string error)
    {
        await using var context = await fixture.CreateContextAsync();
        await context.AddInitScriptAsync($"Intl.DateTimeFormat.prototype.resolvedOptions = () => ({{timeZone: {JsonSerializer.Serialize(zone)}}});");
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync(error);
        await Expect(page.GetByText("2026-11-18", new() { Exact = false })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task LostTransportBlocksCaptureUntilTheSameJournalPageReconnects()
    {
        await using var context = await fixture.CreateContextAsync();
        IWebSocketRoute? active = null;
        var blockReconnect = false;
        await context.RouteAsync("**/_blazor/negotiate?*", route =>
            Volatile.Read(ref blockReconnect) ? route.AbortAsync() : route.ContinueAsync());
        await context.RouteWebSocketAsync(new System.Text.RegularExpressions.Regex(@"/_blazor\?"), async socket =>
        {
            if (Volatile.Read(ref blockReconnect))
                await socket.CloseAsync(new() { Code = 1011 });
            else
            {
                socket.ConnectToServer();
                active = socket;
            }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/capture");
        const string draft = "Capture after reconnect";
        var name = page.GetByLabel("Item name");
        await name.FillAsync(draft);
        await page.EvaluateAsync("window.journalDocument = true");
        var before = fixture.Journal.GetSnapshot().Items.Count;
        Assert.NotNull(active);
        Volatile.Write(ref blockReconnect, true);
        await active.CloseAsync(new() { Code = 1011, Reason = "Synthetic server transport failure" });

        var modal = page.Locator("#components-reconnect-modal");
        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal.GetByText("Connection lost. Reconnecting...", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(modal.Locator("button")).ToBeHiddenAsync();
        var form = page.Locator("form[data-journal-write]");
        await Expect(form).ToHaveAttributeAsync("inert", "");
        await AssertNoConnectionStatusAsync(page);
        Assert.True(await page.EvaluateAsync<bool>("navigator.onLine"));
        Assert.False(await page.EvaluateAsync<bool>("window.bujoPwa.writesAvailable()"));
        var submitted = await form.EvaluateAsync<bool>(
            "element => element.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))");
        Assert.False(submitted);

        Volatile.Write(ref blockReconnect, false);
        await Expect(modal).ToBeHiddenAsync(new() { Timeout = 60000 });
        await Expect(name).ToBeEditableAsync();
        await Expect(name).ToHaveValueAsync(draft);
        Assert.True(await page.EvaluateAsync<bool>("window.journalDocument === true"));
        await AssertOnlineLayoutAsync(page);
        Assert.Equal(before, fixture.Journal.GetSnapshot().Items.Count);
        await Expect(page.GetByText("Item captured.", new() { Exact = true })).ToHaveCountAsync(0);
        await Button(page, "Capture item").ClickAsync();
        await Expect(page.GetByText("Item captured.", new() { Exact = true })).ToBeVisibleAsync();
        var items = fixture.Journal.GetSnapshot().Items;
        Assert.Equal(before + 1, items.Count);
        Assert.Single(items, item => item.Name == draft);
    }

    private static async Task AssertOnlineLayoutAsync(IPage page)
    {
        await AssertNoConnectionStatusAsync(page);
        await Expect(page.Locator("#components-reconnect-modal")).ToBeHiddenAsync();
        await page.WaitForFunctionAsync("() => !!window.bujoPwa");
        Assert.True(await page.EvaluateAsync<bool>("window.bujoPwa.writesAvailable()"));
    }

    private static async Task AssertNoConnectionStatusAsync(IPage page)
    {
        await Expect(page.Locator("[data-connectivity-status], [data-circuit-status]")).ToHaveCountAsync(0);
        await Expect(page.Locator("body")).Not.ToContainTextAsync(new System.Text.RegularExpressions.Regex(
            "Server connection lost or awaiting reconnection|Browser network available|Saves still require server confirmation|" +
            "No journal data is stored offline|retain unsaved input|saved or replayed|Reloading loses unsaved",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static ILocator Link(IPage page, string name) => page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true });
    private static ILocator Button(IPage page, string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static async Task CreateList(IPage page, string name, bool weekly = false)
    {
        await page.GetByLabel("List name").FillAsync(name);
        if (weekly)
        {
            await page.GetByLabel("List type").SelectOptionAsync("Weekly");
            await page.GetByLabel("ISO week-year").FillAsync("2026");
            await page.GetByLabel("ISO week number").FillAsync("1");
        }
        await Button(page, "Create list").ClickAsync();
        await Expect(Link(page, name)).ToBeVisibleAsync();
    }

    private static async Task CaptureAsync(IPage page, string name, string? list = null, string? day = null)
    {
        await page.GotoAsync("/capture");
        await page.GetByLabel("Item name").FillAsync(name);
        if (list is not null)
            await page.GetByLabel("Destination list").SelectOptionAsync(new SelectOptionValue { Label = list });
        if (day is not null)
        {
            await page.GetByLabel("Planned period").SelectOptionAsync("Day");
            await page.GetByLabel("Planned date").FillAsync(day);
        }
        await Button(page, "Capture item").ClickAsync();
        await Expect(Link(page, "View captured item")).ToBeVisibleAsync();
    }

    private static async Task MoveAsync(IPage page, string destination)
    {
        await page.GetByLabel("Move destination").SelectOptionAsync(new SelectOptionValue { Label = destination });
        await Button(page, "Move item").ClickAsync();
    }
}
