using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Application.Tests;

public sealed class ReviewApplicationServiceTests
{
    private static readonly string UserId = $"{Guid.Parse("d1f218cc-a2c7-43ce-8a8b-925393263fc5"):D}:{Guid.Parse("10c0963d-0a47-4a2d-b1ef-81109b4ceec7"):D}";

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

    private static Setup CreateSetup()
    {
        var repository = new InMemoryJournalRepository();
        var clock = new FrozenTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var journal = new JournalService(repository, clock);
        var list = journal.CreateList("Review list", JournalListKind.Custom).Value!;
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
