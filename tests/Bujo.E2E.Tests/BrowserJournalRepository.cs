using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.E2E.Tests;

// Use the real journal command implementation; inject only one lost acknowledgement when requested.
internal sealed class BrowserJournalRepository : IJournalRepository
{
    private readonly InMemoryJournalRepository _inner = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
    private int _loseMoveReply;

    public void LoseNextMoveReply() => Interlocked.Exchange(ref _loseMoveReply, 1);
    public JournalSnapshot GetSnapshot() => _inner.GetSnapshot();
    public DomainResult<JournalSnapshot> RestoreSnapshot(JournalSnapshot snapshot) =>
        _inner.RestoreSnapshot(snapshot);
    public DomainResult<JournalListSnapshot> CreateList(CreateListCommand command) => _inner.CreateList(command);
    public DomainResult<CaptureResult> Capture(CaptureCommand command) => _inner.Capture(command);
    public DomainResult<MoveResult> Move(MoveCommand command)
    {
        var result = _inner.Move(command);
        if (result.IsSuccess && Interlocked.Exchange(ref _loseMoveReply, 0) == 1)
            throw new IOException("Synthetic lost move acknowledgement.");
        return result;
    }

    public DomainResult<OccurrenceSnapshot> Transition(TransitionCommand command) => _inner.Transition(command);
    public DomainResult<JournalListSnapshot> CloseList(ListVersionCommand command) => _inner.CloseList(command);
    public DomainResult<JournalListSnapshot> ReopenList(ListVersionCommand command) => _inner.ReopenList(command);
    public DomainResult<ItemSnapshot> EditItem(EditItemCommand command) => _inner.EditItem(command);
    public DomainResult<ItemSnapshot> ChangePlannedPeriod(ChangePeriodCommand command) => _inner.ChangePlannedPeriod(command);
    public DomainResult<JournalListSnapshot> SetListGoal(SetListGoalCommand command) => _inner.SetListGoal(command);
    public DomainResult<ReviewProposalSnapshot> CreateReviewProposal(CreateReviewProposalCommand command) => _inner.CreateReviewProposal(command);
    public DomainResult<ReviewProposalSnapshot> GetReviewProposal(Guid id) => _inner.GetReviewProposal(id);
    public DomainResult<ReviewProposalSnapshot> SetReviewProposalStatus(ReviewProposalStatusCommand command) => _inner.SetReviewProposalStatus(command);
    public DomainResult<ReviewProposalSnapshot> ApplyReviewProposal(ApplyReviewProposalCommand command) => _inner.ApplyReviewProposal(command);
}
