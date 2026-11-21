using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Bujo.E2E.Tests;

public sealed class ChatKeyboardBrowserTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>
{
    [Fact]
    public async Task ChatComposerSendsOnceWithEnterAndPreservesOtherSubmissionPaths()
    {
        fixture.ChatRuntime.Reset();
        await using var context = await fixture.CreateContextAsync();
        await context.AddInitScriptAsync("""
            class FakeSpeechRecognition {
                constructor() { window.fakeRecognition = this; }
                start() {}
                stop() {}
            }
            Object.defineProperty(window, "SpeechRecognition", {
                configurable: true,
                value: FakeSpeechRecognition
            });
            """);
        var page = await context.NewPageAsync();
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") browserErrors.Add(message.Text);
        };
        await page.GotoAsync("/");
        var input = page.GetByLabel("Message", new() { Exact = true });
        var messages = page.Locator(".chat-message-user .chat-message-text");
        var hint = page.Locator("#chat-keyboard-hint");
        await Expect(input).ToBeEnabledAsync();
        await Expect(hint).ToHaveTextAsync("Press Enter to send. Shift+Enter inserts a new line.");
        await Expect(input).ToHaveAttributeAsync("aria-describedby", "chat-keyboard-hint");

        await input.PressAsync("Enter");
        await Expect(input).ToHaveValueAsync("");
        Assert.Equal(0, fixture.ChatRuntime.InvocationCount);
        await Expect(messages).ToHaveCountAsync(0);

        await input.FillAsync("repeat ignored");
        var repeatedEnterPrevented = await input.EvaluateAsync<bool>("element => { " +
            "const event = new KeyboardEvent('keydown', { key: 'Enter', repeat: true, bubbles: true, cancelable: true }); " +
            "element.dispatchEvent(event); return event.defaultPrevented; }");
        Assert.True(repeatedEnterPrevented);
        await Expect(input).ToHaveValueAsync("repeat ignored");
        Assert.Equal(0, fixture.ChatRuntime.InvocationCount);

        await input.FillAsync("composition");
        var composingEnterPrevented = await input.EvaluateAsync<bool>("element => { " +
            "const event = new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true, cancelable: true }); " +
            "element.dispatchEvent(event); return event.defaultPrevented; }");
        Assert.False(composingEnterPrevented);
        Assert.Equal(0, fixture.ChatRuntime.InvocationCount);
        await Expect(messages).ToHaveCountAsync(0);

        await input.FillAsync("plain enter");
        await input.PressAsync("Enter");
        await Expect(messages).ToHaveCountAsync(1);
        await Expect(messages.Nth(0)).ToHaveTextAsync("plain enter");
        Assert.Equal(1, fixture.ChatRuntime.InvocationCount);

        await input.FillAsync("first line");
        await input.PressAsync("Shift+Enter");
        await page.Keyboard.TypeAsync("second line");
        await Expect(input).ToHaveValueAsync("first line\nsecond line");
        await input.PressAsync("Enter");
        await Expect(messages).ToHaveCountAsync(2);
        await Expect(messages.Nth(1)).ToHaveTextAsync("first line\nsecond line");
        Assert.Equal(2, fixture.ChatRuntime.InvocationCount);

        fixture.ChatRuntime.DelayNextRun();
        await input.FillAsync("in flight");
        await input.PressAsync("Enter");
        await fixture.ChatRuntime.WaitForDelayedRunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Expect(input).ToBeDisabledAsync();
            var busyEnterPrevented = await input.EvaluateAsync<bool>("element => { " +
                "const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }); " +
                "element.dispatchEvent(event); return event.defaultPrevented; }");
            Assert.True(busyEnterPrevented);
            Assert.Equal(3, fixture.ChatRuntime.InvocationCount);
            await Expect(messages).ToHaveCountAsync(3);
        }
        finally
        {
            fixture.ChatRuntime.ReleaseDelayedRun();
        }
        await Expect(input).ToBeEnabledAsync();

        await input.FillAsync("button path");
        await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();
        await Expect(messages).ToHaveCountAsync(4);
        Assert.Equal("button path", fixture.ChatRuntime.Messages[^1]);
        Assert.Equal(4, fixture.ChatRuntime.InvocationCount);

        await page.GetByRole(AriaRole.Button, new() { Name = "Speak", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Listening. Speak once; recognition language is en-US.")).ToBeVisibleAsync();
        Assert.True(await page.EvaluateAsync<bool>("() => typeof window.fakeRecognition?.onresult === 'function'"));
        var voiceInvocation = fixture.ChatRuntime.WaitForNextInvocationAsync();
        await page.EvaluateAsync("""
            () => window.fakeRecognition.onresult({
                resultIndex: 0,
                results: [{ isFinal: true, 0: { transcript: "voice path" } }]
            })
            """);
        await voiceInvocation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(
            fixture.ChatRuntime.InvocationCount == 5,
            $"Runtime calls: {fixture.ChatRuntime.InvocationCount}; browser errors: {string.Join(" | ", browserErrors)}");
        Assert.Equal("voice path", fixture.ChatRuntime.Messages[^1]);
        Assert.Equal(5, fixture.ChatRuntime.InvocationCount);
        Assert.Empty(browserErrors);

        await page.GotoAsync("/today");
        await page.GotoAsync("/");
        input = page.GetByLabel("Message", new() { Exact = true });
        await input.FillAsync("after navigation");
        await input.PressAsync("Enter");
        await Expect(page.Locator(".chat-message-user .chat-message-text")).ToHaveCountAsync(1);
        Assert.Equal("after navigation", fixture.ChatRuntime.Messages[^1]);
        Assert.Equal(6, fixture.ChatRuntime.InvocationCount);
    }
}
