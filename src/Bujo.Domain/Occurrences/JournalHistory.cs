namespace Bujo.Domain;

public enum JournalEventKind
{
    Captured,
    Moved,
    Done,
    Abandoned,
    Reopened,
    ListClosed,
    ListReopened
}

public sealed record JournalEvent(
    long Sequence,
    JournalEventKind Kind,
    DateTimeOffset OccurredAt,
    Guid? ItemId = null,
    Guid? OccurrenceId = null,
    Guid? PreviousOccurrenceId = null,
    Guid? NextOccurrenceId = null,
    Guid? ListId = null,
    Guid? OperationId = null);

public sealed record MoveHistoryEntry(
    long Sequence,
    Guid OperationId,
    Guid ItemId,
    Guid SourceOccurrenceId,
    Guid DestinationOccurrenceId,
    Guid SourceListId,
    Guid DestinationListId,
    DateTimeOffset OccurredAt);
