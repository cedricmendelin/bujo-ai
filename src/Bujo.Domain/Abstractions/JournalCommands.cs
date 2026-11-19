namespace Bujo.Domain;

public sealed record CreateListCommand(
    string Name,
    JournalListKind Kind,
    string? GoalText = null,
    IsoWeekKey? WeekKey = null);

public sealed record CaptureCommand(
    string Name,
    string? Description,
    IReadOnlyList<string>? Tags,
    PlannedPeriod PlannedPeriod,
    Guid ListId,
    long ExpectedListVersion,
    DateTimeOffset OccurredAt,
    ItemType Type = ItemType.Task);

public sealed record CaptureResult(
    ItemSnapshot Item,
    OccurrenceSnapshot Occurrence,
    PeriodConflict? PeriodConflict);

public sealed record MoveCommand(
    Guid OperationId,
    Guid ItemId,
    Guid ExpectedCurrentOccurrenceId,
    Guid DestinationListId,
    long ExpectedItemVersion,
    long ExpectedDestinationListVersion,
    DateTimeOffset OccurredAt);

public sealed record MoveResult(
    ItemResolution Resolution,
    MoveHistoryEntry HistoryEntry,
    PeriodConflict? PeriodConflict);

public sealed record TransitionCommand(
    Guid OccurrenceId,
    OccurrenceAction Action,
    long ExpectedItemVersion,
    long ExpectedListVersion,
    DateTimeOffset OccurredAt);

public sealed record ListVersionCommand(Guid ListId, long ExpectedVersion, DateTimeOffset OccurredAt);

public sealed record ItemVersionCommand(Guid ItemId, long ExpectedVersion);

public sealed record EditItemCommand(
    Guid ItemId,
    long ExpectedVersion,
    string Name,
    string? Description,
    IReadOnlyList<string>? Tags);

public sealed record ChangePeriodCommand(
    Guid ItemId,
    long ExpectedVersion,
    PlannedPeriod PlannedPeriod);

public sealed record SetListGoalCommand(Guid ListId, long ExpectedVersion, string? GoalText);
