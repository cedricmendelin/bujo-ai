using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class IdempotencyTests
{
    [Fact]
    public void Retrying_a_move_returns_the_original_result_without_duplicating_history()
    {
        var fixture = new JournalTestFixture();
        var capture = fixture.Capture("Retry");
        var week47 = fixture.CreateWeekly(2026, 47);
        var week48 = fixture.CreateWeekly(2026, 48);
        var operationId = Guid.NewGuid();

        var first = fixture.Journal.Move(operationId, capture.Item.Id, capture.Occurrence.Id, week47.Id);
        Assert.True(first.IsSuccess);
        fixture.Move(first.Value!.Resolution.Item, first.Value.Resolution.CurrentOccurrence.Id, week48.Id);
        fixture.Transition(fixture.Journal.GetSnapshot().Items.Single().CurrentOccurrenceId, OccurrenceAction.Done);
        var snapshotBeforeRetry = fixture.Journal.GetSnapshot();

        var retry = fixture.Journal.Move(operationId, capture.Item.Id, capture.Occurrence.Id, week47.Id);

        Assert.Same(first.Value, retry.Value);
        Assert.Equal(snapshotBeforeRetry.Occurrences.Count, fixture.Journal.GetSnapshot().Occurrences.Count);
        Assert.Equal(snapshotBeforeRetry.MoveHistory.Count, fixture.Journal.GetSnapshot().MoveHistory.Count);
        Assert.Equal(first.Value.HistoryEntry, retry.Value!.HistoryEntry);
    }

    [Fact]
    public void A_different_operation_with_a_stale_expected_occurrence_conflicts()
    {
        var fixture = new JournalTestFixture();
        var capture = fixture.Capture("Stale");
        var firstList = fixture.CreateWeekly(2026, 47);
        var secondList = fixture.CreateWeekly(2026, 48);
        fixture.Move(capture.Item, capture.Occurrence.Id, firstList.Id);

        var stale = fixture.Journal.Move(Guid.NewGuid(), capture.Item.Id, capture.Occurrence.Id, secondList.Id);

        Assert.False(stale.IsSuccess);
        Assert.Equal(DomainErrorCode.StaleOccurrence, stale.Error!.Code);
        Assert.Single(fixture.Journal.GetSnapshot().MoveHistory);
    }

    [Fact]
    public void Reusing_an_operation_id_for_a_different_move_is_rejected()
    {
        var fixture = new JournalTestFixture();
        var capture = fixture.Capture("Key reuse");
        var firstList = fixture.CreateWeekly(2026, 47);
        var secondList = fixture.CreateWeekly(2026, 48);
        var operationId = Guid.NewGuid();

        Assert.True(fixture.Journal.Move(operationId, capture.Item.Id, capture.Occurrence.Id, firstList.Id).IsSuccess);
        var reused = fixture.Journal.Move(operationId, capture.Item.Id, capture.Occurrence.Id, secondList.Id);

        Assert.Equal(DomainErrorCode.IdempotencyKeyReuse, reused.Error!.Code);
    }
}
