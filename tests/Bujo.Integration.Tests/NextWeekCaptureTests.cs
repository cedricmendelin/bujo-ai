using System.Security.Claims;
using Azure.Core;
using Bujo.Application;
using Bujo.Application.Chat;
using Bujo.Domain;
using Bujo.Host.Chat;
using Bujo.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Bujo.Integration.Tests;

public sealed class NextWeekCaptureTests
{
    private static readonly DateTimeOffset BoundaryInstant =
        new(2020, 12, 27, 23, 30, 0, TimeSpan.Zero);
    private static readonly IsoWeekKey NextLocalWeek = new(2021, 1);
    private const string ConversationId = "next-week-test-thread";

    [Fact]
    public async Task CapturesIntoTheNextLocalIsoWeekAtTheYearBoundary()
    {
        var (journal, runtime) = CreateRuntime();
        var destination = journal.CreateList("Week 01", JournalListKind.Weekly, weekKey: NextLocalWeek).Value!;

        var result = await Run(runtime, "Writing blog article next week", "Europe/Zurich");

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("capture_item", call.Name);
        Assert.True(call.Succeeded);
        Assert.Contains("Writing blog article", result.Text);
        Assert.Contains(destination.Name, result.Text);
        var snapshot = journal.GetSnapshot();
        var item = Assert.Single(snapshot.Items);
        Assert.Equal("Writing blog article", item.Name);
        Assert.Equal(PlannedPeriod.ForWeek(NextLocalWeek), item.PlannedPeriod);
        Assert.Equal(destination.Id, snapshot.Occurrences.Single().ListId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Mars/Olympus")]
    public async Task MissingOrInvalidTimeZoneDoesNotCapture(string? timeZone)
    {
        var (journal, runtime) = CreateRuntime();
        journal.CreateList("Week 01", JournalListKind.Weekly, weekKey: NextLocalWeek);

        var result = await Run(runtime, "Writing blog article next week", timeZone);

        Assert.Empty(result.ToolCalls);
        Assert.Contains("time zone", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(journal.GetSnapshot().Items);
    }

    [Fact]
    public async Task OversizedTaskNameDoesNotCreatePendingStateOrAList()
    {
        var (journal, runtime) = CreateRuntime();

        var result = await Run(runtime, $"{new string('x', 201)} next week", "Europe/Zurich");

        Assert.Empty(result.ToolCalls);
        Assert.Contains("200 characters or fewer", result.Text);
        Assert.Empty(journal.GetSnapshot().Items);
        Assert.Single(journal.GetSnapshot().Lists);
    }

    [Fact]
    public async Task MissingTimeZoneCanBeSuppliedInTheFollowUpMessage()
    {
        var (journal, runtime) = CreateRuntime();
        var destination = journal.CreateList("Week 01", JournalListKind.Weekly, weekKey: NextLocalWeek).Value!;

        var question = await Run(runtime, "Writing blog article next week", null);
        Assert.Empty(question.ToolCalls);
        Assert.Contains("What IANA time zone should I use", question.Text);

        var answer = await Run(runtime, "Europe/Zurich", null);

        Assert.True(Assert.Single(answer.ToolCalls).Succeeded);
        Assert.Contains(destination.Name, answer.Text);
        Assert.Equal(destination.Id, Assert.Single(journal.GetSnapshot().Occurrences).ListId);
        Assert.Equal("Writing blog article", Assert.Single(journal.GetSnapshot().Items).Name);
    }

    [Fact]
    public async Task NextWeekQuestionsAndNonCaptureCommandsDoNotCreateItems()
    {
        var (journal, runtime) = CreateRuntime();
        journal.CreateList("Week 01", JournalListKind.Weekly, weekKey: NextLocalWeek);

        var question = await Run(runtime, "What is planned next week?", "Europe/Zurich");
        var command = await Run(runtime, "move Report to Projects next week", "Europe/Zurich");
        var edit = await Run(runtime, "edit Report to Prepare slides next week", "Europe/Zurich");
        var rename = await Run(runtime, "rename Report to Prepare slides next week", "Europe/Zurich");

        Assert.Empty(question.ToolCalls);
        Assert.Empty(command.ToolCalls);
        Assert.Empty(edit.ToolCalls);
        Assert.Empty(rename.ToolCalls);
        Assert.Empty(journal.GetSnapshot().Items);
    }

    [Fact]
    public async Task MultipleOpenWeeklyListsRequireAnExplicitChoice()
    {
        var (journal, runtime) = CreateRuntime();
        var first = journal.CreateList("A Week", JournalListKind.Weekly, weekKey: NextLocalWeek).Value!;
        var second = journal.CreateList("B Week", JournalListKind.Weekly, weekKey: NextLocalWeek).Value!;

        var question = await Run(runtime, "Writing blog article next week", "Europe/Zurich");

        Assert.Empty(question.ToolCalls);
        Assert.Contains("use 1", question.Text);
        Assert.Empty(journal.GetSnapshot().Items);

        var answer = await Run(runtime, "use 2", "Europe/Zurich");

        Assert.True(Assert.Single(answer.ToolCalls).Succeeded);
        Assert.Contains(second.Name, answer.Text);
        Assert.DoesNotContain(first.Name, answer.Text);
        var snapshot = journal.GetSnapshot();
        Assert.Equal(second.Id, Assert.Single(snapshot.Occurrences).ListId);
        Assert.Equal("Writing blog article", Assert.Single(snapshot.Items).Name);
    }

    [Fact]
    public async Task ClosedAndMissingWeeklyListsRequireCreateConfirmationAndRepeatedConfirmationDoesNotRepeat()
    {
        var (journal, runtime) = CreateRuntime();
        var closed = journal.CreateList("Closed week", JournalListKind.Weekly, weekKey: NextLocalWeek).Value!;
        Assert.True(journal.CloseList(closed.Id).IsSuccess);

        var question = await Run(runtime, "Writing blog article next week", "Europe/Zurich");

        Assert.Empty(question.ToolCalls);
        Assert.Contains("closed", question.Text);
        Assert.Empty(journal.GetSnapshot().Items);
        Assert.Equal(2, journal.GetSnapshot().Lists.Count);

        var unrelated = await Run(runtime, "yes", "Europe/Zurich");
        Assert.Empty(unrelated.ToolCalls);
        Assert.Empty(journal.GetSnapshot().Items);

        var answer = await Run(runtime, "create it", "Europe/Zurich");
        Assert.Equal(["create_list", "capture_item"], answer.ToolCalls.Select(call => call.Name));
        Assert.All(answer.ToolCalls, call => Assert.True(call.Succeeded, call.Name));
        Assert.Contains("Captured 'Writing blog article'", answer.Text);
        Assert.Contains("Week 2021-W01", answer.Text);
        Assert.Single(journal.GetSnapshot().Items);
        Assert.Equal(3, journal.GetSnapshot().Lists.Count);

        var repeated = await Run(runtime, "create it", "Europe/Zurich");
        Assert.Empty(repeated.ToolCalls);
        Assert.Contains("already handled", repeated.Text);
        Assert.Single(journal.GetSnapshot().Items);
        Assert.Equal(3, journal.GetSnapshot().Lists.Count);
    }

    [Fact]
    public async Task ChoiceIsRejectedWhenTheMatchingListSetChanges()
    {
        var (journal, runtime) = CreateRuntime();
        var first = journal.CreateList("A Week", JournalListKind.Weekly, weekKey: NextLocalWeek).Value!;
        journal.CreateList("B Week", JournalListKind.Weekly, weekKey: NextLocalWeek);

        var question = await Run(runtime, "Writing blog article next week", "Europe/Zurich");
        Assert.Empty(question.ToolCalls);
        Assert.True(journal.CloseList(first.Id).IsSuccess);

        var answer = await Run(runtime, "use 1", "Europe/Zurich");

        Assert.Empty(answer.ToolCalls);
        Assert.Contains("changed", answer.Text);
        Assert.Empty(journal.GetSnapshot().Items);
    }

    [Fact]
    public async Task ConfirmationIsScopedToTheAuthenticatedCaller()
    {
        var (journal, runtime) = CreateRuntime();
        await Run(runtime, "Writing blog article next week", "Europe/Zurich");

        var otherCaller = Principal("another-user");
        var answer = await runtime.RunAsync(
            new ChatRunRequest("create it", "Europe/Zurich", [], ConversationId),
            otherCaller);

        Assert.Empty(answer.ToolCalls);
        Assert.Contains("no pending", answer.Text);
        Assert.Empty(journal.GetSnapshot().Items);
        Assert.Single(journal.GetSnapshot().Lists);
    }

    [Fact]
    public async Task ConfirmationIsRejectedWhenTheResolvedLocalWeekHasChanged()
    {
        var (journal, runtime) = CreateRuntime();
        var question = await Run(runtime, "Writing blog article next week", "UTC");
        Assert.Empty(question.ToolCalls);

        var answer = await Run(runtime, "create it", "Europe/Zurich");

        Assert.Empty(answer.ToolCalls);
        Assert.Contains("week changed", answer.Text);
        Assert.Empty(journal.GetSnapshot().Items);
        Assert.Single(journal.GetSnapshot().Lists);
    }

    [Fact]
    public async Task CreatedListRemainsVisibleWhenTaskCaptureFails()
    {
        var repository = new FailingJournalRepository(
            InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid()),
            failCapture: true);
        var (journal, runtime) = CreateRuntime(repository);

        await Run(runtime, "Writing blog article next week", "Europe/Zurich");
        var answer = await Run(runtime, "create it", "Europe/Zurich");

        Assert.Equal(["create_list", "capture_item"], answer.ToolCalls.Select(call => call.Name));
        Assert.True(answer.ToolCalls[0].Succeeded);
        Assert.False(answer.ToolCalls[1].Succeeded);
        Assert.Contains("was not captured", answer.Text);
        Assert.DoesNotContain("Captured '", answer.Text);
        Assert.Empty(journal.GetSnapshot().Items);
        Assert.Equal(2, journal.GetSnapshot().Lists.Count);
    }

    [Fact]
    public async Task ListCreationFailureDoesNotAttemptTaskCapture()
    {
        var repository = new FailingJournalRepository(
            InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid()),
            failCreateList: true);
        var (journal, runtime) = CreateRuntime(repository);

        await Run(runtime, "Writing blog article next week", "Europe/Zurich");
        var answer = await Run(runtime, "create it", "Europe/Zurich");

        var call = Assert.Single(answer.ToolCalls);
        Assert.Equal("create_list", call.Name);
        Assert.False(call.Succeeded);
        Assert.Contains("could not create", answer.Text);
        Assert.Contains("task was not captured", answer.Text);
        Assert.Empty(journal.GetSnapshot().Items);
        Assert.Single(journal.GetSnapshot().Lists);
    }

    private static (JournalApplicationService Journal, ChatRuntime Runtime) CreateRuntime(
        IJournalRepository? repository = null)
    {
        repository ??= InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var clock = new FrozenTimeProvider(BoundaryInstant);
        var journal = new JournalApplicationService(new JournalService(repository, clock), clock);
        var reviews = new ReviewApplicationService(repository, clock);
        var runtime = new ChatRuntime(
            journal,
            reviews,
            Options.Create(new ChatOptions()),
            new TestHttpClientFactory(),
            new TestCredential(),
            new ChatConversationStateStore(clock));
        return (journal, runtime);
    }

    private static Task<ChatRunResult> Run(
        ChatRuntime runtime,
        string message,
        string? timeZone) =>
        runtime.RunAsync(
            new ChatRunRequest(message, timeZone, [], ConversationId),
            Principal());

    private static ClaimsPrincipal Principal(string objectId = "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72") =>
        new(new ClaimsIdentity(
        [
            new Claim("tid", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            new Claim("oid", objectId)
        ], "Test"));

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NeverHandler());
    }

    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The Mock chat runtime must not call an HTTP provider.");
    }

    private sealed class FailingJournalRepository(
        IJournalRepository inner,
        bool failCreateList = false,
        bool failCapture = false) : IJournalRepository
    {
        public JournalSnapshot GetSnapshot() => inner.GetSnapshot();

        public DomainResult<JournalSnapshot> RestoreSnapshot(JournalSnapshot snapshot) =>
            inner.RestoreSnapshot(snapshot);

        public DomainResult<JournalListSnapshot> CreateList(CreateListCommand command) =>
            failCreateList
                ? DomainResult<JournalListSnapshot>.Failure(new DomainError(
                    DomainErrorCode.InvalidInput,
                    "Weekly list creation is unavailable."))
                : inner.CreateList(command);

        public DomainResult<CaptureResult> Capture(CaptureCommand command) =>
            failCapture
                ? DomainResult<CaptureResult>.Failure(new DomainError(
                    DomainErrorCode.InvalidInput,
                    "Task capture is unavailable."))
                : inner.Capture(command);

        public DomainResult<MoveResult> Move(MoveCommand command) => inner.Move(command);

        public DomainResult<OccurrenceSnapshot> Transition(TransitionCommand command) =>
            inner.Transition(command);

        public DomainResult<JournalListSnapshot> CloseList(ListVersionCommand command) =>
            inner.CloseList(command);

        public DomainResult<JournalListSnapshot> ReopenList(ListVersionCommand command) =>
            inner.ReopenList(command);

        public DomainResult<ItemSnapshot> EditItem(EditItemCommand command) => inner.EditItem(command);

        public DomainResult<ItemSnapshot> ChangePlannedPeriod(ChangePeriodCommand command) =>
            inner.ChangePlannedPeriod(command);

        public DomainResult<JournalListSnapshot> SetListGoal(SetListGoalCommand command) =>
            inner.SetListGoal(command);

        public DomainResult<ReviewProposalSnapshot> CreateReviewProposal(CreateReviewProposalCommand command) =>
            inner.CreateReviewProposal(command);

        public DomainResult<ReviewProposalSnapshot> GetReviewProposal(Guid id) =>
            inner.GetReviewProposal(id);

        public DomainResult<ReviewProposalSnapshot> SetReviewProposalStatus(ReviewProposalStatusCommand command) =>
            inner.SetReviewProposalStatus(command);

        public DomainResult<ReviewProposalSnapshot> ApplyReviewProposal(ApplyReviewProposalCommand command) =>
            inner.ApplyReviewProposal(command);
    }
}
