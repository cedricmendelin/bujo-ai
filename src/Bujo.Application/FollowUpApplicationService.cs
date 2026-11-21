using Bujo.Domain;

namespace Bujo.Application;

public sealed class FollowUpApplicationService(IFollowUpRepository repository, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public DomainResult<FollowUpSnapshot> Capture(string title, string condition, DateTimeOffset dueAtUtc,
        string timeZoneId, Guid? itemId, EvidenceSource source) =>
        repository.Create(new CreateFollowUpCommand(Guid.NewGuid(), title, condition, dueAtUtc, timeZoneId, itemId, source));

    public DomainResult<IReadOnlyList<FollowUpSnapshot>> ListDue() => repository.ListDue(_clock.GetUtcNow());

    public DomainResult<FollowUpSnapshot> Get(Guid id) => repository.Get(id);

    public DomainResult<FollowUpSnapshot> RecordEvidence(Guid id, EvidenceSource source,
        FollowUpObservation observation, string? checkedScope) =>
        repository.RecordEvidence(id, source, observation, checkedScope, _clock.GetUtcNow());

    public DomainResult<FollowUpSnapshot> Dismiss(Guid id) => repository.Dismiss(id, _clock.GetUtcNow());

    public DomainResult<FollowUpSnapshot> Defer(Guid id, DateTimeOffset dueAtUtc) =>
        repository.Defer(id, dueAtUtc, _clock.GetUtcNow());

    public DomainResult<FollowUpSnapshot> SetDraft(Guid id, string? draft) => repository.SetDraft(id, draft);

    public DomainResult<FollowUpSnapshot> SetDraftIfCurrent(Guid id, string? expectedDraft, string? draft) =>
        repository.SetDraftIfCurrent(id, expectedDraft, draft);
}
