namespace Bujo.Application;

public sealed record JournalExportDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<JournalListExport> Lists,
    IReadOnlyList<JournalItemExport> Items,
    IReadOnlyList<JournalOccurrenceExport> Occurrences,
    IReadOnlyList<JournalEventExport> History,
    IReadOnlyList<JournalKnowledgeExport> KnowledgeEntries);

public sealed record JournalListExport(
    Guid Id,
    string Name,
    string Kind,
    string State,
    string? GoalText,
    int? WeekYear,
    int? WeekNumber,
    long Version);

public sealed record JournalItemExport(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<string> Tags,
    string PlannedPeriodKind,
    string? PlannedDay,
    int? PlannedWeekYear,
    int? PlannedWeekNumber,
    int? PlannedYear,
    int? PlannedMonth,
    int? PlannedQuarter,
    Guid CurrentOccurrenceId,
    long Version,
    string Type = "Task");

public sealed record JournalOccurrenceExport(
    Guid Id,
    Guid ItemId,
    Guid ListId,
    int Sequence,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? PreviousOccurrenceId,
    Guid? NextOccurrenceId,
    long Version);

public sealed record JournalEventExport(
    long Sequence,
    string Kind,
    DateTimeOffset OccurredAt,
    Guid? ItemId,
    Guid? OccurrenceId,
    Guid? PreviousOccurrenceId,
    Guid? NextOccurrenceId,
    Guid? ListId,
    Guid? OperationId);

public sealed record JournalKnowledgeExport(
    Guid Id,
    string Content,
    string SourceType,
    string? SourceId);
