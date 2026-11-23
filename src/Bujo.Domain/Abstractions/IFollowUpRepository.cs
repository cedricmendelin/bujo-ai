namespace Bujo.Domain;

public interface IFollowUpRepository
{
    DomainResult<FollowUpSnapshot> Create(CreateFollowUpCommand command);
    DomainResult<IReadOnlyList<FollowUpSnapshot>> ListDue(DateTimeOffset nowUtc);
    DomainResult<FollowUpSnapshot> Get(Guid id);
    DomainResult<FollowUpSnapshot> RecordEvidence(Guid id, EvidenceSource source,
        FollowUpObservation observation, string? checkedScope, DateTimeOffset recordedAtUtc);
    DomainResult<FollowUpSnapshot> Dismiss(Guid id, DateTimeOffset occurredAtUtc);
    DomainResult<FollowUpSnapshot> Defer(Guid id, DateTimeOffset dueAtUtc, DateTimeOffset occurredAtUtc);
    DomainResult<FollowUpSnapshot> SetDraft(Guid id, string? draft);
    DomainResult<FollowUpSnapshot> SetDraftIfCurrent(Guid id, string? expectedDraft, string? draft);
}

public sealed record CreateFollowUpCommand(Guid Id, string Title, string Condition,
    DateTimeOffset DueAtUtc, string TimeZoneId, Guid? ItemId, EvidenceSource Source);
