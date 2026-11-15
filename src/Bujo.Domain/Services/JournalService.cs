namespace Bujo.Domain;

public sealed class JournalService
{
    private readonly IJournalRepository _repository;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;

    public JournalService(
        IJournalRepository repository,
        TimeProvider? clock = null,
        string ianaTimeZoneId = "UTC")
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _clock = clock ?? TimeProvider.System;
        if (string.IsNullOrWhiteSpace(ianaTimeZoneId))
        {
            throw new DomainException(DomainErrorCode.InvalidTimeZone, "An IANA time zone is required.");
        }

        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new DomainException(DomainErrorCode.InvalidTimeZone, $"Unknown IANA time zone: {ianaTimeZoneId}.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new DomainException(DomainErrorCode.InvalidTimeZone, $"Invalid time zone: {ianaTimeZoneId}.");
        }
    }

    public JournalSnapshot GetSnapshot() => _repository.GetSnapshot();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _timeZone).DateTime);

    public DomainResult<JournalListSnapshot> CreateList(
        string name,
        JournalListKind kind,
        string? goalText = null,
        IsoWeekKey? weekKey = null) =>
        _repository.CreateList(new CreateListCommand(name, kind, goalText, weekKey));

    public DomainResult<CaptureResult> Capture(
        string name,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        PlannedPeriod? plannedPeriod = null,
        Guid? listId = null)
    {
        var snapshot = _repository.GetSnapshot();
        var list = listId is { } id
            ? snapshot.Lists.SingleOrDefault(candidate => candidate.Id == id)
            : snapshot.Lists.SingleOrDefault(candidate => candidate.Kind == JournalListKind.FutureLog);

        if (list is null)
        {
            return DomainResult<CaptureResult>.Failure(
                new DomainError(DomainErrorCode.NotFound, "The destination list was not found."));
        }

        return _repository.Capture(new CaptureCommand(
            name,
            description,
            tags,
            plannedPeriod ?? PlannedPeriod.Unplanned,
            list.Id,
            list.Version,
            UtcNow));
    }

    public DomainResult<MoveResult> Move(
        Guid operationId,
        Guid itemId,
        Guid expectedCurrentOccurrenceId,
        Guid destinationListId)
    {
        var snapshot = _repository.GetSnapshot();
        var item = snapshot.Items.SingleOrDefault(candidate => candidate.Id == itemId);
        var destination = snapshot.Lists.SingleOrDefault(candidate => candidate.Id == destinationListId);
        if (item is null || destination is null)
        {
            return DomainResult<MoveResult>.Failure(
                new DomainError(DomainErrorCode.NotFound, "The item or destination list was not found."));
        }

        return _repository.Move(new MoveCommand(
            operationId,
            itemId,
            expectedCurrentOccurrenceId,
            destinationListId,
            item.Version,
            destination.Version,
            UtcNow));
    }

    public DomainResult<OccurrenceSnapshot> Transition(Guid occurrenceId, OccurrenceAction action)
    {
        var snapshot = _repository.GetSnapshot();
        var occurrence = snapshot.Occurrences.SingleOrDefault(candidate => candidate.Id == occurrenceId);
        if (occurrence is null)
        {
            return DomainResult<OccurrenceSnapshot>.Failure(
                new DomainError(DomainErrorCode.NotFound, "The occurrence was not found."));
        }

        var item = snapshot.Items.Single(candidate => candidate.Id == occurrence.ItemId);
        var list = snapshot.Lists.Single(candidate => candidate.Id == occurrence.ListId);
        return _repository.Transition(new TransitionCommand(
            occurrenceId,
            action,
            item.Version,
            list.Version,
            UtcNow));
    }

    public DomainResult<JournalListSnapshot> CloseList(Guid listId)
    {
        var list = _repository.GetSnapshot().Lists.SingleOrDefault(candidate => candidate.Id == listId);
        return list is null
            ? DomainResult<JournalListSnapshot>.Failure(new DomainError(DomainErrorCode.NotFound, "The list was not found."))
            : _repository.CloseList(new ListVersionCommand(listId, list.Version, UtcNow));
    }

    public DomainResult<JournalListSnapshot> ReopenList(Guid listId)
    {
        var list = _repository.GetSnapshot().Lists.SingleOrDefault(candidate => candidate.Id == listId);
        return list is null
            ? DomainResult<JournalListSnapshot>.Failure(new DomainError(DomainErrorCode.NotFound, "The list was not found."))
            : _repository.ReopenList(new ListVersionCommand(listId, list.Version, UtcNow));
    }

    public DomainResult<ItemSnapshot> EditItem(
        Guid itemId,
        string name,
        string? description,
        IReadOnlyList<string>? tags)
    {
        var item = _repository.GetSnapshot().Items.SingleOrDefault(candidate => candidate.Id == itemId);
        return item is null
            ? DomainResult<ItemSnapshot>.Failure(new DomainError(DomainErrorCode.NotFound, "The item was not found."))
            : _repository.EditItem(new EditItemCommand(itemId, item.Version, name, description, tags));
    }

    public DomainResult<ItemSnapshot> ChangePlannedPeriod(Guid itemId, PlannedPeriod plannedPeriod)
    {
        var item = _repository.GetSnapshot().Items.SingleOrDefault(candidate => candidate.Id == itemId);
        return item is null
            ? DomainResult<ItemSnapshot>.Failure(new DomainError(DomainErrorCode.NotFound, "The item was not found."))
            : _repository.ChangePlannedPeriod(new ChangePeriodCommand(itemId, item.Version, plannedPeriod));
    }

    public DomainResult<JournalListSnapshot> SetListGoal(Guid listId, string? goalText)
    {
        var list = _repository.GetSnapshot().Lists.SingleOrDefault(candidate => candidate.Id == listId);
        return list is null
            ? DomainResult<JournalListSnapshot>.Failure(new DomainError(DomainErrorCode.NotFound, "The list was not found."))
            : _repository.SetListGoal(new SetListGoalCommand(listId, list.Version, goalText));
    }

    public DomainResult<ItemResolution> Resolve(Guid occurrenceId) => _repository.GetSnapshot().Resolve(occurrenceId);

    private DateTimeOffset UtcNow => _clock.GetUtcNow().ToUniversalTime();
}
