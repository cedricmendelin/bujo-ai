using Bujo.Application;
using Bujo.Domain;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Bujo.E2E.Tests;

public sealed class FollowUpBrowserTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureDueReviewCorrectDeferAndDismissWithoutSending(bool chatEnabled)
    {
        fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 18, 23, 30, 0, TimeSpan.Zero);
        await using var context = await fixture.CreateContextAsync(chatEnabled: chatEnabled);
        var externalRequests = new List<string>();
        await context.RouteAsync("https://**/*", route =>
        {
            externalRequests.Add(route.Request.Url);
            return route.AbortAsync();
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Follow-ups due" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = chatEnabled ? "Chat" : "Chat unavailable", Exact = true })).ToBeVisibleAsync();
        Assert.Empty((await page.RunAxe()).Violations);
        await page.GetByRole(AriaRole.Link, new() { Name = "Capture a follow-up" }).ClickAsync();
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync($"Synthetic follow-up {chatEnabled}");
        await page.GetByLabel("Condition").FillAsync("If no response is recorded");
        await page.GetByLabel("Due date and time", new() { Exact = false }).FillAsync("2026-11-19T00:15");
        Assert.False(await page.GetByLabel("Evidence source").EvaluateAsync<bool>("element => element.checkValidity()"));
        await page.GetByLabel("Evidence source").SelectOptionAsync("Manual");
        Assert.Empty((await page.RunAxe()).Violations);
        await Button(page, "Save follow-up").ClickAsync();
        await Expect(page.GetByText("Follow-up saved.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GotoAsync("/");
        var due = page.GetByRole(AriaRole.Link, new() { Name = $"Synthetic follow-up {chatEnabled}" });
        await Expect(due).ToBeVisibleAsync();
        await Expect(due.Locator("..")).ToContainTextAsync("2026-11-19 00:15 (Europe/Zurich)");
        await due.ClickAsync();
        await Expect(page.GetByText("Evidence unavailable", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Empty((await page.RunAxe()).Violations);
        await page.GetByLabel("What did you observe?").SelectOptionAsync("NoReplySeen");
        await Button(page, "Record evidence").ClickAsync();
        await Expect(page.GetByText("Evidence recorded.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("Evidence unavailable", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByLabel("Checked scope", new() { Exact = false }).FillAsync("Synthetic inbox and spam");
        await Button(page, "Record evidence").ClickAsync();
        await Expect(page.GetByText("No reply seen in the checked scope", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByLabel("What did you observe?").SelectOptionAsync("ReplySeen");
        await Button(page, "Record evidence").ClickAsync();
        await Expect(page.GetByText("Reply seen", new() { Exact = true }).First).ToBeVisibleAsync();
        await Expect(page.Locator("ol li")).ToHaveCountAsync(3);
        await page.GetByLabel("Draft text").FillAsync("Synthetic draft for review");
        await Button(page, "Save draft").ClickAsync();
        await Expect(page.GetByText("Draft saved. Nothing was sent.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true })).ToHaveCountAsync(0);
        await page.GetByLabel("New due date and time", new() { Exact = false }).FillAsync("2026-11-20T10:00");
        await Button(page, "Defer").ClickAsync();
        await Expect(page.GetByText("Follow-up deferred.", new() { Exact = true })).ToBeVisibleAsync();
        var id = Guid.Parse(new Uri(page.Url).Segments.Last());
        var saved = new FollowUpApplicationService(fixture.FollowUps, fixture.Clock).Get(id).Value!;
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), saved.DueAtUtc);
        Assert.Equal("Synthetic draft for review", saved.Draft);
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = $"Synthetic follow-up {chatEnabled}" })).ToHaveCountAsync(0);
        fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        await page.ReloadAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = $"Synthetic follow-up {chatEnabled}" }).ClickAsync();
        await Button(page, "Dismiss follow-up").ClickAsync();
        await Expect(page.GetByText("Follow-up dismissed.", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(FollowUpStatus.Dismissed, new FollowUpApplicationService(fixture.FollowUps, fixture.Clock).Get(id).Value!.Status);
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = $"Synthetic follow-up {chatEnabled}" })).ToHaveCountAsync(0);
        Assert.Empty(externalRequests);
    }

    [Fact]
    public async Task ClockChangesAreRejectedAndOfflineWritesDoNotPersist()
    {
        fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 18, 23, 30, 0, TimeSpan.Zero);
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/followups/new");
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("Synthetic clock test");
        await page.GetByLabel("Condition").FillAsync("Synthetic condition");
        await page.GetByLabel("Evidence source").SelectOptionAsync("Manual");
        var due = page.GetByLabel("Due date and time", new() { Exact = false });
        await due.EvaluateAsync("element => { element.value = '2026-02-30T12:00'; element.dispatchEvent(new Event('change', { bubbles: true })); }");
        Assert.False(await due.EvaluateAsync<bool>("element => element.checkValidity()"));
        await Button(page, "Save follow-up").ClickAsync();
        Assert.DoesNotContain(new FollowUpApplicationService(fixture.FollowUps, fixture.Clock).ListDue().Value!,
            item => item.Title == "Synthetic clock test");
        await due.FillAsync("2026-03-29T02:30");
        await Button(page, "Save follow-up").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("does not exist");
        await due.FillAsync("2026-10-25T02:30");
        await Button(page, "Save follow-up").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("occurs twice");
        await due.FillAsync("2026-11-19T00:15");
        await context.SetOfflineAsync(true);
        try
        {
            await Expect(page.Locator("form[data-journal-write]")).ToHaveAttributeAsync("aria-disabled", "true");
            await page.Locator("form[data-journal-write]").EvaluateAsync("form => form.requestSubmit()");
            Assert.DoesNotContain(new FollowUpApplicationService(fixture.FollowUps, fixture.Clock).ListDue().Value!,
                item => item.Title == "Synthetic clock test");
        }
        finally { await context.SetOfflineAsync(false); }
        await Expect(page.Locator("form[data-journal-write]")).Not.ToHaveAttributeAsync("aria-disabled", "true");
        await Button(page, "Save follow-up").ClickAsync();
        await Expect(page.GetByText("Follow-up saved.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Review saved follow-up" }).ClickAsync();
        await page.GetByLabel("New due date and time", new() { Exact = false }).FillAsync("2026-03-29T02:30");
        await Button(page, "Defer").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("does not exist");
        await page.GetByLabel("New due date and time", new() { Exact = false }).FillAsync("2026-10-25T02:30");
        await Button(page, "Defer").ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("occurs twice");
    }

    [Fact]
    public async Task UnauthorizedVisitorsCannotSeeOrOpenFollowUps()
    {
        var service = new FollowUpApplicationService(fixture.FollowUps, fixture.Clock);
        var item = service.Capture("Synthetic private follow-up", "Synthetic condition",
            fixture.Clock.UtcNow, "Europe/Zurich", null, EvidenceSource.Manual).Value!;
        await using var context = await fixture.CreateContextAsync(identity: null);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await Expect(page.GetByText("Synthetic private follow-up")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Capture a follow-up" })).ToHaveCountAsync(0);
        await page.GotoAsync($"/followups/{item.Id}");
        await Expect(page.GetByText("Synthetic private follow-up")).ToHaveCountAsync(0);
        Assert.Equal(FollowUpStatus.Pending, service.Get(item.Id).Value!.Status);
    }

    [Fact]
    public async Task ChangedAuthenticationClosesTheReviewWithoutSaving()
    {
        var service = new FollowUpApplicationService(fixture.FollowUps, fixture.Clock);
        var item = service.Capture("Synthetic session follow-up", "Synthetic condition",
            fixture.Clock.UtcNow, "Europe/Zurich", null, EvidenceSource.Manual).Value!;
        await using var context = await fixture.CreateContextAsync(observeAuthentication: true);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"/followups/{item.Id}");
        await page.GetByLabel("Draft text").FillAsync("Unsaved synthetic draft");
        await context.ClearCookiesAsync();
        fixture.Authentication.Revoke();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in to your journal" })).ToBeVisibleAsync();
        Assert.Null(service.Get(item.Id).Value!.Draft);
    }

    [Fact]
    public async Task JournalBackupRoundTripsFollowUpsInTheBrowserFixture()
    {
        var service = new FollowUpApplicationService(fixture.FollowUps, fixture.Clock);
        var item = service.Capture("Synthetic backup follow-up", "Synthetic condition",
            fixture.Clock.UtcNow, "Europe/Zurich", null, EvidenceSource.Manual).Value!;
        Assert.True(service.SetDraft(item.Id, "Original synthetic draft").IsSuccess);
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/knowledge");
        var downloadTask = page.WaitForDownloadAsync();
        await Button(page, "Export journal backup").ClickAsync();
        await using var stream = await (await downloadTask).CreateReadStreamAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.True(service.SetDraft(item.Id, "Changed synthetic draft").IsSuccess);
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await page.Locator("#knowledge-backup-file").SetInputFilesAsync(new FilePayload
        {
            Name = "journal.json",
            MimeType = "application/json",
            Buffer = buffer.ToArray()
        });
        await Expect(page.GetByText("Journal backup restored.", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal("Original synthetic draft", service.Get(item.Id).Value!.Draft);
    }

    [Fact]
    public async Task ConcurrentDraftEditsPreserveLocalTextAndCannotOverwriteNewerDraft()
    {
        var service = new FollowUpApplicationService(fixture.FollowUps, fixture.Clock);
        var item = service.Capture("Synthetic concurrent draft", "Synthetic condition",
            fixture.Clock.UtcNow, "Europe/Zurich", null, EvidenceSource.Manual).Value!;
        Assert.True(service.SetDraft(item.Id, "Initial draft").IsSuccess);
        await using var context = await fixture.CreateContextAsync();
        var first = await context.NewPageAsync();
        var second = await context.NewPageAsync();
        await first.GotoAsync($"/followups/{item.Id}");
        await second.GotoAsync($"/followups/{item.Id}");
        await second.GetByLabel("Draft text").FillAsync("Updated clean draft");
        await Button(second, "Save draft").ClickAsync();
        await Expect(second.GetByText("Draft saved. Nothing was sent.", new() { Exact = true })).ToBeVisibleAsync();
        await first.GetByLabel("What did you observe?").SelectOptionAsync("ReplySeen");
        await Button(first, "Record evidence").ClickAsync();
        await Expect(first.GetByLabel("Draft text")).ToHaveValueAsync("Updated clean draft");

        await first.GetByLabel("Draft text").FillAsync("First unsaved edit");
        await second.GetByLabel("Draft text").FillAsync("Second saved edit");
        await Button(second, "Save draft").ClickAsync();
        for (var attempt = 0; attempt < 50 && service.Get(item.Id).Value!.Draft != "Second saved edit"; attempt++)
            await Task.Delay(100);
        Assert.Equal("Second saved edit", service.Get(item.Id).Value!.Draft);

        await Button(first, "Save draft").ClickAsync();
        await Expect(first.GetByText("The saved draft changed elsewhere.", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(first.GetByLabel("Draft text")).ToHaveValueAsync("First unsaved edit");
        await Expect(Button(first, "Save draft")).ToBeDisabledAsync();
        Assert.Equal("Second saved edit", service.Get(item.Id).Value!.Draft);
        await Button(first, "Load latest draft and discard my edit").ClickAsync();
        await Expect(first.GetByLabel("Draft text")).ToHaveValueAsync("Second saved edit");

        await first.GetByLabel("Draft text").FillAsync("Another unsaved edit");
        await second.GetByLabel("Draft text").FillAsync("Third saved edit");
        await Button(second, "Save draft").ClickAsync();
        for (var attempt = 0; attempt < 50 && service.Get(item.Id).Value!.Draft != "Third saved edit"; attempt++)
            await Task.Delay(100);
        Assert.Equal("Third saved edit", service.Get(item.Id).Value!.Draft);
        await first.GetByLabel("What did you observe?").SelectOptionAsync("ReplySeen");
        await Button(first, "Record evidence").ClickAsync();
        await Expect(first.GetByText("The saved draft changed elsewhere.", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(first.GetByLabel("Draft text")).ToHaveValueAsync("Another unsaved edit");
        await Expect(Button(first, "Save draft")).ToBeDisabledAsync();
        Assert.Equal("Third saved edit", service.Get(item.Id).Value!.Draft);
    }

    [Fact]
    public async Task DismissalInAnotherSessionPreservesUnsavedDraftForCopying()
    {
        var service = new FollowUpApplicationService(fixture.FollowUps, fixture.Clock);
        var item = service.Capture("Synthetic dismissed draft", "Synthetic condition",
            fixture.Clock.UtcNow, "Europe/Zurich", null, EvidenceSource.Manual).Value!;
        Assert.True(service.SetDraft(item.Id, "Previously saved draft").IsSuccess);
        await using var context = await fixture.CreateContextAsync();
        var first = await context.NewPageAsync();
        var second = await context.NewPageAsync();
        await first.GotoAsync($"/followups/{item.Id}");
        await second.GotoAsync($"/followups/{item.Id}");
        await first.GetByLabel("Draft text").FillAsync("Unsaved text to copy");
        await Button(second, "Dismiss follow-up").ClickAsync();
        await Expect(second.GetByText("Follow-up dismissed.", new() { Exact = true })).ToBeVisibleAsync();

        await Button(first, "Save draft").ClickAsync();
        await Expect(first.GetByRole(AriaRole.Alert)).ToContainTextAsync("not pending");
        await Button(first, "Refresh journal").ClickAsync();
        await Expect(first.GetByText("Your unsaved draft was not saved", new() { Exact = false })).ToBeVisibleAsync();
        var unsaved = first.GetByLabel("Your unsaved draft (read-only)");
        await Expect(unsaved).ToHaveValueAsync("Unsaved text to copy");
        await Expect(unsaved).ToHaveAttributeAsync("readonly", "");
        await Expect(Button(first, "Save draft")).ToHaveCountAsync(0);
        await Expect(first.GetByText("Previously saved draft", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal("Previously saved draft", service.Get(item.Id).Value!.Draft);
        Assert.Equal(FollowUpStatus.Dismissed, service.Get(item.Id).Value!.Status);
        Assert.Empty((await first.RunAxe()).Violations);
    }

    private static ILocator Button(IPage page, string name) =>
        page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
}
