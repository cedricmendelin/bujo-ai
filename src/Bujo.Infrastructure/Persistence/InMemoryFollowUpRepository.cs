using Bujo.Domain;

namespace Bujo.Infrastructure.Persistence;

public sealed class InMemoryFollowUpRepository(InMemoryJournalRepository journal) : IFollowUpRepository
{
    public DomainResult<FollowUpSnapshot> Create(CreateFollowUpCommand command)
    {
        lock (journal.SyncRoot)
        {
            try
            {
                var followUp = FollowUp.Create(command.Id, command.Title, command.Condition, command.DueAtUtc,
                    command.TimeZoneId, command.ItemId, command.Source);
                if (command.ItemId is { } itemId && !journal.GetSnapshot().Items.Any(item => item.Id == itemId))
                    return Failure<FollowUpSnapshot>(DomainErrorCode.NotFound, "The linked item was not found.");
                if (!journal.FollowUps.TryAdd(followUp.Id, followUp))
                    return Failure<FollowUpSnapshot>(DomainErrorCode.InvariantViolation, "The follow-up already exists.");
                return DomainResult<FollowUpSnapshot>.Success(followUp.ToSnapshot());
            }
            catch (DomainException exception)
            {
                return DomainResult<FollowUpSnapshot>.Failure(exception.ToError());
            }
        }
    }

    public DomainResult<IReadOnlyList<FollowUpSnapshot>> ListDue(DateTimeOffset nowUtc)
    {
        lock (journal.SyncRoot)
        {
            IReadOnlyList<FollowUpSnapshot> due = journal.FollowUps.Values
                .Where(item => item.Status == FollowUpStatus.Pending && item.DueAtUtc <= nowUtc)
                .OrderBy(item => item.DueAtUtc).ThenBy(item => item.Id)
                .Select(item => item.ToSnapshot()).ToArray();
            return DomainResult<IReadOnlyList<FollowUpSnapshot>>.Success(due);
        }
    }

    public DomainResult<FollowUpSnapshot> Get(Guid id)
    {
        lock (journal.SyncRoot)
            return journal.FollowUps.TryGetValue(id, out var item)
                ? DomainResult<FollowUpSnapshot>.Success(item.ToSnapshot())
                : Failure<FollowUpSnapshot>(DomainErrorCode.NotFound, "The follow-up was not found.");
    }

    public DomainResult<FollowUpSnapshot> RecordEvidence(Guid id, EvidenceSource source,
        FollowUpObservation observation, string? checkedScope, DateTimeOffset recordedAtUtc) =>
        Mutate(id, item => item.RecordEvidence(source, observation, checkedScope, recordedAtUtc));

    public DomainResult<FollowUpSnapshot> Dismiss(Guid id, DateTimeOffset occurredAtUtc) =>
        Mutate(id, item => item.Dismiss(occurredAtUtc));

    public DomainResult<FollowUpSnapshot> Defer(Guid id, DateTimeOffset dueAtUtc, DateTimeOffset occurredAtUtc) =>
        Mutate(id, item => item.Defer(dueAtUtc, occurredAtUtc));

    public DomainResult<FollowUpSnapshot> SetDraft(Guid id, string? draft) =>
        Mutate(id, item => item.SetDraft(draft));

    public DomainResult<FollowUpSnapshot> SetDraftIfCurrent(Guid id, string? expectedDraft, string? draft) =>
        Mutate(id, item =>
        {
            if (!string.Equals(item.Draft, expectedDraft, StringComparison.Ordinal))
                throw new DomainException(DomainErrorCode.ConcurrencyConflict, "The follow-up draft has changed.");
            item.SetDraft(draft);
        });

    private DomainResult<FollowUpSnapshot> Mutate(Guid id, Action<FollowUp> action)
    {
        lock (journal.SyncRoot)
        {
            if (!journal.FollowUps.TryGetValue(id, out var item))
                return Failure<FollowUpSnapshot>(DomainErrorCode.NotFound, "The follow-up was not found.");
            try
            {
                action(item);
                return DomainResult<FollowUpSnapshot>.Success(item.ToSnapshot());
            }
            catch (DomainException exception)
            {
                return DomainResult<FollowUpSnapshot>.Failure(exception.ToError());
            }
        }
    }

    private static DomainResult<T> Failure<T>(DomainErrorCode code, string message) =>
        DomainResult<T>.Failure(new DomainError(code, message));
}
