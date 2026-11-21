namespace Bujo.Domain;

// Zero is deliberately not a source: capture must explicitly select Manual.
public enum EvidenceSource
{
    Manual = 1
}

public enum FollowUpObservation
{
    ReplySeen = 1,
    NoReplySeen = 2
}

public enum FollowUpOutcome
{
    EvidenceUnavailable,
    ReplySeen,
    NoReplySeenInCheckedScope
}

public enum FollowUpStatus
{
    Pending,
    Dismissed,
    Resolved
}

public enum FollowUpChangeKind
{
    Dismissed,
    Deferred
}

public sealed record FollowUpEvidence(
    long Sequence,
    EvidenceSource Source,
    FollowUpObservation Observation,
    string? CheckedScope,
    DateTimeOffset RecordedAtUtc);

public sealed record FollowUpChange(
    long Sequence,
    FollowUpChangeKind Kind,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? PreviousDueAtUtc,
    DateTimeOffset? NewDueAtUtc);

public sealed record FollowUpSnapshot(
    Guid Id,
    string Title,
    string Condition,
    DateTimeOffset DueAtUtc,
    string TimeZoneId,
    Guid? ItemId,
    EvidenceSource Source,
    FollowUpStatus Status,
    string? Draft,
    IReadOnlyList<FollowUpEvidence> Evidence,
    IReadOnlyList<FollowUpChange> Changes)
{
    public FollowUpOutcome Outcome => FollowUpOutcomeDerivation.Derive(Source, Evidence);
}

public sealed record FollowUpBackupSnapshot(int SchemaVersion, IReadOnlyList<FollowUpSnapshot> FollowUps);

public static class FollowUpOutcomeDerivation
{
    public static FollowUpOutcome Derive(
        EvidenceSource source,
        IReadOnlyList<FollowUpEvidence> evidence)
    {
        // Inspect only the most recent entry. An earlier observation must not
        // override a correction, even when the correction cannot be interpreted.
        var latest = evidence.Count == 0 ? null : evidence.MaxBy(entry => entry.Sequence);
        if (source != EvidenceSource.Manual || latest is null || latest.Source != source ||
            latest.Sequence < 1 || evidence.Count(entry => entry.Sequence == latest.Sequence) != 1)
        {
            return FollowUpOutcome.EvidenceUnavailable;
        }

        return latest.Observation switch
        {
            FollowUpObservation.ReplySeen => FollowUpOutcome.ReplySeen,
            FollowUpObservation.NoReplySeen when !string.IsNullOrWhiteSpace(latest.CheckedScope) =>
                FollowUpOutcome.NoReplySeenInCheckedScope,
            _ => FollowUpOutcome.EvidenceUnavailable
        };
    }
}

public sealed class FollowUp
{
    private readonly List<FollowUpEvidence> _evidence = [];
    private readonly List<FollowUpChange> _changes = [];

    private FollowUp(
        Guid id,
        string title,
        string condition,
        DateTimeOffset dueAtUtc,
        string timeZoneId,
        Guid? itemId,
        EvidenceSource source)
    {
        Id = id;
        Title = title;
        Condition = condition;
        DueAtUtc = dueAtUtc;
        TimeZoneId = timeZoneId;
        ItemId = itemId;
        Source = source;
    }

    public Guid Id { get; }
    public string Title { get; }
    public string Condition { get; }
    public DateTimeOffset DueAtUtc { get; private set; }
    public string TimeZoneId { get; }
    public Guid? ItemId { get; }
    public EvidenceSource Source { get; }
    public FollowUpStatus Status { get; private set; }
    public string? Draft { get; private set; }
    public IReadOnlyList<FollowUpEvidence> Evidence => _evidence.AsReadOnly();
    public IReadOnlyList<FollowUpChange> Changes => _changes.AsReadOnly();
    public FollowUpOutcome Outcome => FollowUpOutcomeDerivation.Derive(Source, Evidence);

    internal FollowUpSnapshot ToSnapshot() => new(
        Id, Title, Condition, DueAtUtc, TimeZoneId, ItemId, Source, Status, Draft,
        _evidence.ToArray(), _changes.ToArray());

    internal static FollowUp FromSnapshot(FollowUpSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Evidence is null || snapshot.Changes is null ||
            snapshot.DueAtUtc.Offset != TimeSpan.Zero)
        {
            throw DomainLimits.Invalid("The follow-up snapshot is invalid.");
        }

        if (snapshot.Changes.Any(change => change is null))
        {
            throw DomainLimits.Invalid("The follow-up change history is invalid.");
        }
        var firstDeferral = snapshot.Changes.FirstOrDefault(change => change.Kind == FollowUpChangeKind.Deferred);
        var initialDue = firstDeferral?.PreviousDueAtUtc ?? snapshot.DueAtUtc;
        var result = Create(snapshot.Id, snapshot.Title, snapshot.Condition, initialDue,
            snapshot.TimeZoneId, snapshot.ItemId, snapshot.Source);
        result.SetDraft(snapshot.Draft);
        foreach (var evidence in snapshot.Evidence)
        {
            if (evidence is null || evidence.Sequence != result._evidence.Count + 1L ||
                evidence.RecordedAtUtc.Offset != TimeSpan.Zero)
            {
                throw DomainLimits.Invalid("The follow-up evidence history is invalid.");
            }

            result.RecordEvidence(evidence.Source, evidence.Observation, evidence.CheckedScope, evidence.RecordedAtUtc);
        }

        foreach (var change in snapshot.Changes)
        {
            if (change is null || change.Sequence != result._changes.Count + 1L ||
                change.OccurredAtUtc.Offset != TimeSpan.Zero)
            {
                throw DomainLimits.Invalid("The follow-up change history is invalid.");
            }

            if (change.Kind == FollowUpChangeKind.Deferred &&
                change.PreviousDueAtUtc == result.DueAtUtc && change.NewDueAtUtc is { Offset: var offset } due &&
                offset == TimeSpan.Zero)
            {
                result.Defer(due, change.OccurredAtUtc);
            }
            else if (change.Kind == FollowUpChangeKind.Dismissed &&
                     change.PreviousDueAtUtc is null && change.NewDueAtUtc is null)
            {
                result.Dismiss(change.OccurredAtUtc);
            }
            else
            {
                throw DomainLimits.Invalid("The follow-up change history is invalid.");
            }
        }

        if (result.Status != snapshot.Status || result.DueAtUtc != snapshot.DueAtUtc)
        {
            throw DomainLimits.Invalid("The follow-up status or due date is invalid.");
        }

        return result;
    }

    internal static FollowUp Create(
        Guid id,
        string title,
        string condition,
        DateTimeOffset dueAt,
        string timeZoneId,
        Guid? itemId,
        EvidenceSource source)
    {
        if (id == Guid.Empty || itemId == Guid.Empty)
        {
            throw DomainLimits.Invalid("A valid follow-up ID and optional item ID are required.");
        }

        if (source != EvidenceSource.Manual)
        {
            throw DomainLimits.Invalid("An available evidence source must be selected.");
        }

        var zone = DomainLimits.RequiredText(timeZoneId, DomainLimits.FollowUpTimeZone, "Time zone");
        try
        {
            if (!TimeZoneInfo.FindSystemTimeZoneById(zone).HasIanaId)
            {
                throw DomainLimits.Invalid("An IANA time zone is required.");
            }
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new DomainException(DomainErrorCode.InvalidTimeZone, "The time zone is invalid.");
        }

        return new FollowUp(
            id,
            DomainLimits.RequiredText(title, DomainLimits.FollowUpTitle, "Title"),
            DomainLimits.RequiredVerbatimText(condition, DomainLimits.FollowUpCondition, "Condition"),
            dueAt.ToUniversalTime(),
            zone,
            itemId,
            source);
    }

    internal void RecordEvidence(
        EvidenceSource source,
        FollowUpObservation observation,
        string? checkedScope,
        DateTimeOffset recordedAt)
    {
        RequirePending();
        if (source != EvidenceSource.Manual || source != Source ||
            observation is not (FollowUpObservation.ReplySeen or FollowUpObservation.NoReplySeen))
        {
            throw DomainLimits.Invalid("The evidence source or observation is invalid.");
        }

        var scope = DomainLimits.OptionalText(checkedScope, DomainLimits.FollowUpCheckedScope, "Checked scope");
        _evidence.Add(new FollowUpEvidence(_evidence.Count + 1L, source, observation, scope, recordedAt.ToUniversalTime()));
    }

    internal void Dismiss(DateTimeOffset occurredAt)
    {
        RequirePending();
        Status = FollowUpStatus.Dismissed;
        _changes.Add(new FollowUpChange(_changes.Count + 1L, FollowUpChangeKind.Dismissed,
            occurredAt.ToUniversalTime(), null, null));
    }

    internal void Defer(DateTimeOffset dueAt, DateTimeOffset occurredAt)
    {
        RequirePending();
        var newDueAt = dueAt.ToUniversalTime();
        _changes.Add(new FollowUpChange(_changes.Count + 1L, FollowUpChangeKind.Deferred,
            occurredAt.ToUniversalTime(), DueAtUtc, newDueAt));
        DueAtUtc = newDueAt;
    }

    internal void SetDraft(string? draft)
    {
        RequirePending();
        if (draft is not null && draft.Length > DomainLimits.FollowUpDraft)
        {
            throw DomainLimits.Invalid($"Draft must not exceed {DomainLimits.FollowUpDraft} characters.");
        }

        Draft = string.IsNullOrWhiteSpace(draft) ? null : draft;
    }

    private void RequirePending()
    {
        if (Status != FollowUpStatus.Pending)
        {
            throw new DomainException(DomainErrorCode.InvalidTransition, "The follow-up is not pending.");
        }
    }
}
