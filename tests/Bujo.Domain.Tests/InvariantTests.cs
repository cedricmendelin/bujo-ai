using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class InvariantTests
{
    [Fact]
    public void Seeded_move_sequences_preserve_one_open_occurrence_and_a_linear_ordered_history()
    {
        var fixture = new JournalTestFixture();
        var lists = Enumerable.Range(1, 8)
            .Select(week => fixture.CreateWeekly(2026, week))
            .Prepend(fixture.FutureLog)
            .ToArray();
        var random = new Random(1729);
        var items = Enumerable.Range(0, 8).Select(index => fixture.Capture($"Item {index}")).ToArray();

        foreach (var (capture, index) in items.Select((capture, index) => (capture, index)))
        {
            var item = capture.Item;
            var current = capture.Occurrence;
            for (var hop = 0; hop < 6; hop++)
            {
                if (hop == 2 && index % 2 == 0)
                {
                    fixture.Transition(current.Id, OccurrenceAction.Done);
                    fixture.Transition(current.Id, OccurrenceAction.Reopen);
                }

                var candidates = lists.Where(list => list.Id != current.ListId).ToArray();
                var destination = candidates[random.Next(candidates.Length)];
                var moved = fixture.Move(item, current.Id, destination.Id);
                item = moved.Resolution.Item;
                current = moved.Resolution.CurrentOccurrence;
            }

            fixture.Transition(current.Id, index % 2 == 0 ? OccurrenceAction.Done : OccurrenceAction.Abandon);
        }

        var snapshot = fixture.Journal.GetSnapshot();
        foreach (var item in snapshot.Items)
        {
            var occurrences = snapshot.Occurrences.Where(occurrence => occurrence.ItemId == item.Id).ToArray();
            Assert.InRange(occurrences.Count(occurrence => occurrence.State == OccurrenceState.Open), 0, 1);
            Assert.Equal(occurrences.Length, occurrences.Max(occurrence => occurrence.Sequence));

            var current = occurrences.Single(occurrence => occurrence.Id == item.CurrentOccurrenceId);
            var visited = new HashSet<Guid>();
            while (current.PreviousOccurrenceId is { } previousId)
            {
                Assert.True(visited.Add(current.Id));
                var previous = occurrences.Single(occurrence => occurrence.Id == previousId);
                Assert.Equal(current.Id, previous.NextOccurrenceId);
                Assert.True(current.Sequence > previous.Sequence);
                current = previous;
            }

            Assert.True(visited.Add(current.Id));
            Assert.Equal(occurrences.Length, visited.Count);
        }

        Assert.Equal(
            snapshot.History.Select(entry => entry.Sequence).Order(),
            snapshot.History.Select(entry => entry.Sequence));
        Assert.Equal(
            snapshot.History.Count(entry => entry.Kind == JournalEventKind.Moved),
            snapshot.MoveHistory.Count);
        foreach (var move in snapshot.MoveHistory)
        {
            var source = snapshot.Occurrences.Single(occurrence => occurrence.Id == move.SourceOccurrenceId);
            var destination = snapshot.Occurrences.Single(occurrence => occurrence.Id == move.DestinationOccurrenceId);
            Assert.Equal(destination.Id, source.NextOccurrenceId);
            Assert.Equal(source.Id, destination.PreviousOccurrenceId);
            Assert.Equal(OccurrenceState.Moved, source.State);
        }
    }

    [Fact]
    public void Domain_assembly_has_no_infrastructure_references()
    {
        var references = typeof(JournalService).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Azure.", StringComparison.Ordinal) ||
            reference.Name.StartsWith("System.Net.Http", StringComparison.Ordinal));
    }
}
