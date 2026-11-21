using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Application.Tests;

public sealed class ReviewApplicationServiceTests
{
    private static readonly string UserId = $"{Guid.Parse("d1f218cc-a2c7-43ce-8a8b-925393263fc5"):D}:{Guid.Parse("10c0963d-0a47-4a2d-b1ef-81109b4ceec7"):D}";

    [Theory]
    [InlineData(JournalListKind.Monthly)]
    [InlineData(JournalListKind.Quarterly)]
    [InlineData(JournalListKind.Yearly)]
    public void CalendarReviewRequiresExactApprovalAndPreservesReplayStaleAndExpiry(JournalListKind kind)
    {
        var setup = CreateSetup(kind);
        var task = setup.Journal.Capture("Task", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [Change(task.Item, task.Occurrence, ReviewDisposition.Done)], true);
        Assert.Equal(64, proposal.Digest.Length);
        Assert.Equal(DomainErrorCode.ApprovalDenied, setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, McpActor()).Error!.Code);
        Assert.Equal(DomainErrorCode.ProposalDigestMismatch, setup.Reviews.ApproveProposal(proposal.Id, new string('0', 64), PwaActor()).Error!.Code);
        Assert.True(setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor()).IsSuccess);
        var applied = setup.Reviews.ApplyProposal(proposal.Id, McpActor());
        var after = setup.Repository.GetSnapshot();
        Assert.True(applied.IsSuccess, applied.Error?.Message);
        Assert.Equal(JournalListState.Closed, after.Lists.Single().State);
        Assert.Equal(OccurrenceState.Done, after.Occurrences.Single().State);
        Assert.Equal(applied.Value, setup.Reviews.ApplyProposal(proposal.Id, PwaActor()).Value);
        AssertJournalUnchanged(after, setup.Repository.GetSnapshot());

        var staleSetup = CreateSetup(kind);
        var staleItem = staleSetup.Journal.Capture("Stale", listId: staleSetup.List.Id).Value!;
        var stale = CreateProposal(staleSetup, [Change(staleItem.Item, staleItem.Occurrence, ReviewDisposition.Done)]);
        Assert.True(staleSetup.Reviews.ApproveProposal(stale.Id, stale.Digest, PwaActor()).IsSuccess);
        Assert.True(staleSetup.Journal.Capture("Concurrent", listId: staleSetup.List.Id).IsSuccess);
        var before = staleSetup.Repository.GetSnapshot();
        Assert.Equal(DomainErrorCode.ConcurrencyConflict, staleSetup.Reviews.ApplyProposal(stale.Id, McpActor()).Error!.Code);
        AssertJournalUnchanged(before, staleSetup.Repository.GetSnapshot());

        var expirySetup = CreateSetup(kind);
        var expiryItem = expirySetup.Journal.Capture("Expiry", listId: expirySetup.List.Id).Value!;
        var expiry = CreateProposal(expirySetup, [Change(expiryItem.Item, expiryItem.Occurrence, ReviewDisposition.Done)]);
        expirySetup.Clock.SetUtcNow(expiry.ExpiresAt.AddTicks(1));
        Assert.Equal(DomainErrorCode.ProposalExpired, expirySetup.Reviews.ApproveProposal(expiry.Id, expiry.Digest, PwaActor()).Error!.Code);
    }

    [Fact]
    public void ReviewClosureKeepsThoughtWithoutDispositionButStillRequiresTask()
    {
        var setup = CreateSetup();
        var thought = setup.Journal.Capture("A thought", listId: setup.List.Id, type: ItemType.Thought).Value!;
        var task = setup.Journal.Capture("A task", listId: setup.List.Id).Value!;
        var listVersion = setup.Repository.GetSnapshot().Lists.Single(list => list.Id == setup.List.Id).Version;
        var blocked = setup.Reviews.CreateProposal(
            new ReviewProposalRequest(setup.List.Id, listVersion, true, []), McpActor());

        Assert.Equal(DomainErrorCode.UnresolvedOccurrences, blocked.Error!.Code);
        Assert.Equal([task.Occurrence.Id], blocked.Error.RelatedIds);
        Assert.Contains(setup.Reviews.GetListReview(setup.List.Id).Value!.Unfinished,
            entry => entry.Item.Id == thought.Item.Id && entry.Item.Type == ItemType.Thought);

        var proposal = setup.Reviews.CreateProposal(
            new ReviewProposalRequest(setup.List.Id, listVersion, true,
                [Change(task.Item, task.Occurrence, ReviewDisposition.Done)]), McpActor());
        Assert.True(proposal.IsSuccess, proposal.Error?.Message);
        Assert.True(setup.Reviews.ApproveProposal(proposal.Value!.Id, proposal.Value.Digest, PwaActor()).IsSuccess);
        var applied = setup.Reviews.ApplyProposal(proposal.Value.Id, McpActor());
        Assert.True(applied.IsSuccess, applied.Error?.Message);
        var snapshot = setup.Repository.GetSnapshot();
        Assert.Equal(JournalListState.Closed, snapshot.Lists.Single(list => list.Id == setup.List.Id).State);
        Assert.Equal(OccurrenceState.Open, snapshot.Resolve(thought.Occurrence.Id).Value!.CurrentOccurrence.State);
        Assert.Equal(ItemType.Thought, snapshot.Resolve(thought.Occurrence.Id).Value!.Item.Type);
        Assert.Equal(OccurrenceState.Done, snapshot.Resolve(task.Occurrence.Id).Value!.CurrentOccurrence.State);
    }

    [Fact]
    public void ReviewCanCloseThoughtOnlyListAndRejectsTaskDispositionsForThoughts()
    {
        var setup = CreateSetup();
        var thought = setup.Journal.Capture("Reflection", listId: setup.List.Id, type: ItemType.Thought).Value!;
        var listVersion = setup.Repository.GetSnapshot().Lists.Single(list => list.Id == setup.List.Id).Version;
        foreach (var action in new[] { ReviewDisposition.Done, ReviewDisposition.Abandon })
        {
            var invalid = setup.Reviews.CreateProposal(new ReviewProposalRequest(
                setup.List.Id, listVersion, false, [Change(thought.Item, thought.Occurrence, action)]), McpActor());
            Assert.Equal(DomainErrorCode.InvalidInput, invalid.Error!.Code);
        }

        var proposal = setup.Reviews.CreateProposal(
            new ReviewProposalRequest(setup.List.Id, listVersion, true, []), McpActor());
        Assert.True(proposal.IsSuccess, proposal.Error?.Message);
        Assert.True(setup.Reviews.ApproveProposal(proposal.Value!.Id, proposal.Value.Digest, PwaActor()).IsSuccess);
        Assert.True(setup.Reviews.ApplyProposal(proposal.Value.Id, McpActor()).IsSuccess);
        Assert.Equal(OccurrenceState.Open,
            setup.Repository.GetSnapshot().Resolve(thought.Occurrence.Id).Value!.CurrentOccurrence.State);
    }

    [Fact]
    public void ClosureConflictDiagnosticsReportOnlyNewUnresolvedTasks()
    {
        var setup = CreateSetup();
        var proposal = setup.Reviews.CreateProposal(
            new ReviewProposalRequest(setup.List.Id, setup.List.Version, true, []), McpActor()).Value!;
        Assert.True(setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor()).IsSuccess);
        var thought = setup.Journal.Capture("New thought", listId: setup.List.Id, type: ItemType.Thought).Value!;
        var task = setup.Journal.Capture("New task", listId: setup.List.Id).Value!;

        var conflict = setup.Reviews.ApplyProposal(proposal.Id, McpActor());

        Assert.Equal(DomainErrorCode.ConcurrencyConflict, conflict.Error!.Code);
        Assert.Contains($"Unresolved occurrence {task.Occurrence.Id}", conflict.Error.Message);
        Assert.DoesNotContain(thought.Occurrence.Id.ToString(), conflict.Error.Message);
    }

    [Fact]
    public void ClosingReviewMayAlsoMoveThoughtToAnotherList()
    {
        var setup = CreateSetup();
        var destination = setup.Journal.CreateList("Destination", JournalListKind.Custom).Value!;
        var thought = setup.Journal.Capture("Reflection", listId: setup.List.Id, type: ItemType.Thought).Value!;
        var task = setup.Journal.Capture("Task", listId: setup.List.Id).Value!;
        var listVersion = setup.Repository.GetSnapshot().Lists.Single(list => list.Id == setup.List.Id).Version;
        var proposal = setup.Reviews.CreateProposal(new ReviewProposalRequest(
            setup.List.Id, listVersion, true,
            [
                MoveChange(thought.Item, thought.Occurrence, destination),
                Change(task.Item, task.Occurrence, ReviewDisposition.Done)
            ]), McpActor());

        Assert.True(proposal.IsSuccess, proposal.Error?.Message);
        Assert.True(setup.Reviews.ApproveProposal(proposal.Value!.Id, proposal.Value.Digest, PwaActor()).IsSuccess);
        var applied = setup.Reviews.ApplyProposal(proposal.Value.Id, McpActor());
        Assert.True(applied.IsSuccess, applied.Error?.Message);
        var resolution = setup.Repository.GetSnapshot().Resolve(thought.Occurrence.Id).Value!;
        Assert.Equal(ItemType.Thought, resolution.Item.Type);
        Assert.Equal(destination.Id, resolution.CurrentList.Id);
        Assert.Equal(OccurrenceState.Moved, resolution.SourceOccurrence.State);
        Assert.Equal(OccurrenceState.Open, resolution.CurrentOccurrence.State);
    }

    [Fact]
    public void ApprovedMultiItemProposalAppliesOnceAndCanBeAppliedByMcpAfterPwaApproval()
    {
        var setup = CreateSetup();
        var first = setup.Journal.Capture("Done item", listId: setup.List.Id).Value!;
        var second = setup.Journal.Capture("Abandoned item", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [
            Change(first.Item, first.Occurrence, ReviewDisposition.Done),
            Change(second.Item, second.Occurrence, ReviewDisposition.Abandon)
        ], requestClose: true);

        Assert.False(setup.Reviews.ApplyProposal(proposal.Id, McpActor()).IsSuccess);
        Assert.Equal(2, proposal.Changes.Count);
        Assert.Equal(64, proposal.Digest.Length);
        Assert.All(proposal.Changes, change => Assert.NotEqual(Guid.Empty, change.OperationId));

        var approved = setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor());
        var applied = setup.Reviews.ApplyProposal(proposal.Id, McpActor());
        var replay = setup.Reviews.ApplyProposal(proposal.Id, PwaActor());
        var snapshot = setup.Repository.GetSnapshot();

        Assert.True(approved.IsSuccess, approved.Error?.Message);
        Assert.True(applied.IsSuccess, applied.Error?.Message);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(ReviewProposalStatus.Applied, applied.Value!.Status);
        Assert.Equal(applied.Value, replay.Value);
        Assert.Equal(JournalListState.Closed, snapshot.Lists.Single(list => list.Id == setup.List.Id).State);
        Assert.Equal(OccurrenceState.Done, snapshot.Occurrences.Single(item => item.Id == first.Occurrence.Id).State);
        Assert.Equal(OccurrenceState.Abandoned, snapshot.Occurrences.Single(item => item.Id == second.Occurrence.Id).State);
        Assert.Equal(2, snapshot.Occurrences.Count);
    }

    [Fact]
    public void ApprovedMultiItemMoveProposalPreservesMovedSourcesAndResolvesDestinations()
    {
        var setup = CreateSetup();
        var destination = setup.Journal.CreateList("Destination list", JournalListKind.Custom).Value!;
        var first = setup.Journal.Capture("First item", listId: setup.List.Id).Value!;
        var second = setup.Journal.Capture("Second item", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [
            MoveChange(first.Item, first.Occurrence, destination),
            MoveChange(second.Item, second.Occurrence, destination)
        ], requestClose: true);

        Assert.True(setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor()).IsSuccess);
        var applied = setup.Reviews.ApplyProposal(proposal.Id, McpActor());
        var snapshot = setup.Repository.GetSnapshot();

        Assert.True(applied.IsSuccess, applied.Error?.Message);
        Assert.Equal(JournalListState.Closed, snapshot.Lists.Single(list => list.Id == setup.List.Id).State);
        Assert.All(
            snapshot.Occurrences.Where(occurrence => occurrence.ListId == setup.List.Id),
            occurrence => Assert.Equal(OccurrenceState.Moved, occurrence.State));
        Assert.All(
            snapshot.Items,
            item => Assert.Equal(destination.Id, snapshot.Resolve(item.CurrentOccurrenceId).Value!.CurrentList.Id));
    }

    [Fact]
    public void FailedListClosureRollsBackEarlierProposalDispositions()
    {
        var clock = new FrozenTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var journal = new JournalService(repository, clock);
        var futureLog = repository.GetSnapshot().Lists.Single();
        var captured = journal.Capture("Future item", listId: futureLog.Id).Value!;
        var reviews = new ReviewApplicationService(repository, clock);
        var proposal = reviews.CreateProposal(
            new ReviewProposalRequest(
                futureLog.Id,
                repository.GetSnapshot().Lists.Single().Version,
                true,
                [Change(captured.Item, captured.Occurrence, ReviewDisposition.Done)]),
            McpActor()).Value!;
        Assert.True(reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor()).IsSuccess);
        var beforeApply = repository.GetSnapshot();

        var apply = reviews.ApplyProposal(proposal.Id, McpActor());

        Assert.Equal(DomainErrorCode.FutureLogCannotClose, apply.Error!.Code);
        AssertJournalUnchanged(beforeApply, repository.GetSnapshot());
        Assert.Equal(
            ReviewProposalStatus.Approved,
            reviews.GetProposal(proposal.Id, McpActor()).Value!.Status);
    }

    [Fact]
    public void ApprovalRequiresCreatorPwaSessionAndExactDigest()
    {
        var setup = CreateSetup();
        var captured = setup.Journal.Capture("Review item", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [Change(captured.Item, captured.Occurrence, ReviewDisposition.Done)]);

        var deniedMcp = setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, McpActor());
        var deniedOther = setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor(UserId + ":other"));
        var mismatched = setup.Reviews.ApproveProposal(proposal.Id, new string('0', 64), PwaActor());
        var applied = setup.Reviews.ApplyProposal(proposal.Id, McpActor());

        Assert.Equal(DomainErrorCode.ApprovalDenied, deniedMcp.Error!.Code);
        Assert.Equal(DomainErrorCode.ApprovalDenied, deniedOther.Error!.Code);
        Assert.Equal(DomainErrorCode.ProposalDigestMismatch, mismatched.Error!.Code);
        Assert.Equal(DomainErrorCode.ApprovalRequired, applied.Error!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAndRejectDoNotChangeJournalData(bool reject)
    {
        var setup = CreateSetup();
        var captured = setup.Journal.Capture("Review item", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [Change(captured.Item, captured.Occurrence, ReviewDisposition.Done)]);
        var before = setup.Repository.GetSnapshot();
        var result = reject
            ? setup.Reviews.RejectProposal(proposal.Id, McpActor())
            : setup.Reviews.CancelProposal(proposal.Id, McpActor());

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(reject ? ReviewProposalStatus.Rejected : ReviewProposalStatus.Cancelled, result.Value!.Status);
        AssertJournalUnchanged(before, setup.Repository.GetSnapshot());
        Assert.Equal(DomainErrorCode.ProposalTerminal, setup.Reviews.ApplyProposal(proposal.Id, McpActor()).Error!.Code);
    }

    [Fact]
    public void StaleProposalConflictsWithoutApplyingAnyDispositionAndCannotBeReplayed()
    {
        var setup = CreateSetup();
        var first = setup.Journal.Capture("First item", listId: setup.List.Id).Value!;
        var second = setup.Journal.Capture("Second item", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [
            Change(first.Item, first.Occurrence, ReviewDisposition.Done),
            Change(second.Item, second.Occurrence, ReviewDisposition.Abandon)
        ]);
        Assert.True(setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor()).IsSuccess);
        Assert.True(setup.Journal.Transition(first.Occurrence.Id, OccurrenceAction.Done).IsSuccess);
        var beforeApply = setup.Repository.GetSnapshot();

        var conflict = setup.Reviews.ApplyProposal(proposal.Id, McpActor());
        var replay = setup.Reviews.ApplyProposal(proposal.Id, McpActor());
        var stored = setup.Reviews.GetProposal(proposal.Id, McpActor());

        Assert.Equal(DomainErrorCode.ConcurrencyConflict, conflict.Error!.Code);
        Assert.Equal(DomainErrorCode.ConcurrencyConflict, replay.Error!.Code);
        Assert.Contains(first.Item.Id.ToString(), conflict.Error.Message);
        Assert.Contains($"expected version {first.Item.Version}", conflict.Error.Message);
        Assert.Equal(ReviewProposalStatus.Conflicted, stored.Value!.Status);
        AssertJournalUnchanged(beforeApply, setup.Repository.GetSnapshot());
    }

    [Fact]
    public void RequestCloseRequiresDispositionForEveryUnfinishedOccurrence()
    {
        var setup = CreateSetup();
        var first = setup.Journal.Capture("First item", listId: setup.List.Id).Value!;
        setup.Journal.Capture("Second item", listId: setup.List.Id);

        var result = setup.Reviews.CreateProposal(
            new ReviewProposalRequest(
                setup.List.Id,
                setup.List.Version + 2,
                true,
                [Change(first.Item, first.Occurrence, ReviewDisposition.Done)]),
            McpActor());

        Assert.Equal(DomainErrorCode.UnresolvedOccurrences, result.Error!.Code);
    }

    [Fact]
    public void StaleListVersionRequiresFreshProposalAndApproval()
    {
        var setup = CreateSetup();
        var captured = setup.Journal.Capture("Review item", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [Change(captured.Item, captured.Occurrence, ReviewDisposition.Done)]);
        Assert.True(setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor()).IsSuccess);
        setup.Journal.Capture("Concurrent item", listId: setup.List.Id);
        var beforeApply = setup.Repository.GetSnapshot();

        var conflict = setup.Reviews.ApplyProposal(proposal.Id, McpActor());
        var stored = setup.Reviews.GetProposal(proposal.Id, McpActor());

        Assert.Equal(DomainErrorCode.ConcurrencyConflict, conflict.Error!.Code);
        Assert.Contains("expected version", conflict.Error.Message);
        Assert.Equal(ReviewProposalStatus.Conflicted, stored.Value!.Status);
        AssertJournalUnchanged(beforeApply, setup.Repository.GetSnapshot());
    }

    [Fact]
    public async Task ConcurrentApprovalsForTheSameOccurrenceApplyAtMostOnce()
    {
        var setup = CreateSetup();
        var captured = setup.Journal.Capture("Concurrent review item", listId: setup.List.Id).Value!;
        var first = CreateProposal(setup, [Change(captured.Item, captured.Occurrence, ReviewDisposition.Done)]);
        var second = CreateProposal(setup, [Change(captured.Item, captured.Occurrence, ReviewDisposition.Abandon)]);
        Assert.True(setup.Reviews.ApproveProposal(first.Id, first.Digest, PwaActor()).IsSuccess);
        Assert.True(setup.Reviews.ApproveProposal(second.Id, second.Digest, PwaActor()).IsSuccess);

        var results = await Task.WhenAll(
            Task.Run(() => setup.Reviews.ApplyProposal(first.Id, McpActor())),
            Task.Run(() => setup.Reviews.ApplyProposal(second.Id, McpActor())));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error?.Code == DomainErrorCode.ConcurrencyConflict);
        Assert.Single(setup.Repository.GetSnapshot().Occurrences);
        Assert.Single(
            setup.Repository.GetSnapshot().Occurrences,
            item => item.State is OccurrenceState.Done or OccurrenceState.Abandoned);
    }

    [Fact]
    public void ExpiredProposalCannotBeApprovedOrApplied()
    {
        var setup = CreateSetup();
        var captured = setup.Journal.Capture("Expired review item", listId: setup.List.Id).Value!;
        var proposal = CreateProposal(setup, [Change(captured.Item, captured.Occurrence, ReviewDisposition.Done)]);
        setup.Clock.SetUtcNow(proposal.ExpiresAt.AddTicks(1));

        var expired = setup.Reviews.GetProposal(proposal.Id, McpActor());
        var approval = setup.Reviews.ApproveProposal(proposal.Id, proposal.Digest, PwaActor());
        var apply = setup.Reviews.ApplyProposal(proposal.Id, McpActor());

        Assert.True(expired.IsSuccess);
        Assert.Equal(ReviewProposalStatus.Expired, expired.Value!.Status);
        Assert.Equal(DomainErrorCode.ProposalExpired, approval.Error!.Code);
        Assert.Equal(DomainErrorCode.ProposalExpired, apply.Error!.Code);
        Assert.Equal(OccurrenceState.Open, setup.Repository.GetSnapshot().Occurrences.Single().State);
    }

    private static Setup CreateSetup(JournalListKind kind = JournalListKind.Custom)
    {
        var repository = new InMemoryJournalRepository();
        var clock = new FrozenTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var journal = new JournalService(repository, clock);
        var key = kind == JournalListKind.Custom ? (JournalPeriodKey?)null :
            JournalPeriodKey.FromDate(kind, new DateOnly(2026, 11, 18));
        var list = journal.CreateList("Review list", kind, periodKey: key).Value!;
        return new Setup(repository, journal, new ReviewApplicationService(repository, clock), list, clock);
    }

    private static ReviewProposalSnapshot CreateProposal(
        Setup setup,
        IReadOnlyList<ReviewChangeRequest> changes,
        bool requestClose = false) =>
        setup.Reviews.CreateProposal(
            new ReviewProposalRequest(
                setup.List.Id,
                setup.Repository.GetSnapshot().Lists.Single(list => list.Id == setup.List.Id).Version,
                requestClose,
                changes),
            McpActor()).Value!;

    private static ReviewChangeRequest Change(
        ItemSnapshot item,
        OccurrenceSnapshot occurrence,
        ReviewDisposition action) =>
        new(item.Id, occurrence.Id, item.Version, action);

    private static ReviewChangeRequest MoveChange(
        ItemSnapshot item,
        OccurrenceSnapshot occurrence,
        JournalListSnapshot destination) =>
        new(
            item.Id,
            occurrence.Id,
            item.Version,
            ReviewDisposition.Move,
            destination.Id,
            destination.Version);

    private static ReviewActor PwaActor(string? userId = null) =>
        new(userId ?? UserId, ReviewProposalSource.Pwa, Interactive: true);

    private static ReviewActor McpActor() => new(UserId, ReviewProposalSource.Mcp, Interactive: false);

    private static void AssertJournalUnchanged(JournalSnapshot expected, JournalSnapshot actual)
    {
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.Lists, actual.Lists);
        Assert.Equal(expected.Occurrences, actual.Occurrences);
        Assert.Equal(expected.History, actual.History);
    }

    private sealed record Setup(
        InMemoryJournalRepository Repository,
        JournalService Journal,
        ReviewApplicationService Reviews,
        JournalListSnapshot List,
        FrozenTimeProvider Clock);

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }
}
