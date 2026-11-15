namespace Bujo.Domain;

public enum OccurrenceState
{
    Open,
    Done,
    Abandoned,
    Moved
}

public enum OccurrenceAction
{
    Done,
    Abandon,
    Reopen
}

public sealed class Occurrence
{
    private Occurrence(Guid id, Guid itemId, Guid listId, int sequence, Guid? previousOccurrenceId, DateTimeOffset createdAt)
    {
        Id = id;
        ItemId = itemId;
        ListId = listId;
        Sequence = sequence;
        PreviousOccurrenceId = previousOccurrenceId;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; }

    public Guid ItemId { get; }

    public Guid ListId { get; }

    public int Sequence { get; }

    public OccurrenceState State { get; private set; } = OccurrenceState.Open;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? PreviousOccurrenceId { get; }

    public Guid? NextOccurrenceId { get; private set; }

    public long Version { get; private set; }

    internal static Occurrence Create(
        Guid id,
        Guid itemId,
        Guid listId,
        int sequence,
        Guid? previousOccurrenceId,
        DateTimeOffset createdAt) =>
        new(id, itemId, listId, sequence, previousOccurrenceId, createdAt);

    public void Complete(DateTimeOffset occurredAt) => TransitionFromOpen(OccurrenceState.Done, occurredAt);

    public void Abandon(DateTimeOffset occurredAt) => TransitionFromOpen(OccurrenceState.Abandoned, occurredAt);

    public void Move(DateTimeOffset occurredAt) => TransitionFromOpen(OccurrenceState.Moved, occurredAt);

    public void Reopen(bool isCurrent, bool listIsOpen, DateTimeOffset occurredAt)
    {
        if (!listIsOpen)
        {
            throw new DomainException(DomainErrorCode.ListClosed, "An occurrence on a closed list cannot be reopened.");
        }

        if (!isCurrent)
        {
            throw new DomainException(DomainErrorCode.StaleOccurrence, "Only the current occurrence can be reopened.");
        }

        if (State is not (OccurrenceState.Done or OccurrenceState.Abandoned))
        {
            throw new DomainException(DomainErrorCode.InvalidTransition, $"A {State} occurrence cannot be reopened.");
        }

        State = OccurrenceState.Open;
        UpdatedAt = occurredAt.ToUniversalTime();
        Version++;
    }

    internal void LinkNext(Occurrence next)
    {
        if (NextOccurrenceId is not null || next.PreviousOccurrenceId != Id ||
            next.ItemId != ItemId || next.Sequence <= Sequence)
        {
            throw new DomainException(DomainErrorCode.InvariantViolation, "Occurrence links must form a forward linear chain.");
        }

        NextOccurrenceId = next.Id;
        Version++;
    }

    private void TransitionFromOpen(OccurrenceState nextState, DateTimeOffset occurredAt)
    {
        if (State != OccurrenceState.Open)
        {
            throw new DomainException(DomainErrorCode.InvalidTransition, $"A {State} occurrence cannot become {nextState}.");
        }

        State = nextState;
        UpdatedAt = occurredAt.ToUniversalTime();
        Version++;
    }
}
