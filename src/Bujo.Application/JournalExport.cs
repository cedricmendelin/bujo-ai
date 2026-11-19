using System.Globalization;
using Bujo.Domain;

namespace Bujo.Application;

public sealed class JournalExport(TimeProvider clock) : IJournalExport
{
    public JournalExportDocument Create(JournalSnapshot snapshot) =>
        new(
            1,
            clock.GetUtcNow(),
            snapshot.Lists
                .OrderBy(list => list.Id)
                .Select(list => new JournalListExport(
                    list.Id,
                    list.Name,
                    list.Kind.ToString(),
                    list.State.ToString(),
                    list.GoalText,
                    list.WeekKey?.Year,
                    list.WeekKey?.Week,
                    list.Version))
                .ToArray(),
            snapshot.Items
                .OrderBy(item => item.Id)
                .Select(item => new JournalItemExport(
                    item.Id,
                    item.Name,
                    item.Description,
                    item.Tags.Select(tag => tag.Value).ToArray(),
                    item.PlannedPeriod.Kind.ToString(),
                    item.PlannedPeriod.Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    item.PlannedPeriod.Week?.Year,
                    item.PlannedPeriod.Week?.Week,
                    item.PlannedPeriod.Year,
                    item.PlannedPeriod.Month,
                    item.PlannedPeriod.Quarter,
                    item.CurrentOccurrenceId,
                    item.Version))
                .ToArray(),
            snapshot.Occurrences
                .OrderBy(occurrence => occurrence.Sequence)
                .ThenBy(occurrence => occurrence.Id)
                .Select(occurrence => new JournalOccurrenceExport(
                    occurrence.Id,
                    occurrence.ItemId,
                    occurrence.ListId,
                    occurrence.Sequence,
                    occurrence.State.ToString(),
                    occurrence.CreatedAt,
                    occurrence.UpdatedAt,
                    occurrence.PreviousOccurrenceId,
                    occurrence.NextOccurrenceId,
                    occurrence.Version))
                .ToArray(),
            snapshot.History
                .OrderBy(entry => entry.Sequence)
                .Select(entry => new JournalEventExport(
                    entry.Sequence,
                    entry.Kind.ToString(),
                    entry.OccurredAt,
                    entry.ItemId,
                    entry.OccurrenceId,
                    entry.PreviousOccurrenceId,
                    entry.NextOccurrenceId,
                    entry.ListId,
                    entry.OperationId))
                .ToArray(),
            []);
}
