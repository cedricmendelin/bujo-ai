using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class FollowUpTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingEvidenceAndNoReplyWithoutScopeAreUnavailable()
    {
        var followUp = Create();
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable, followUp.Outcome);

        followUp.RecordEvidence(EvidenceSource.Manual, FollowUpObservation.NoReplySeen, " ", Now);
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable, followUp.Outcome);
    }

    [Fact]
    public void LatestEntryWinsEvenForEqualOrEarlierTimestamps()
    {
        var followUp = Create();
        followUp.RecordEvidence(EvidenceSource.Manual, FollowUpObservation.NoReplySeen, "Inbox and spam", Now);
        Assert.Equal(FollowUpOutcome.NoReplySeenInCheckedScope, followUp.Outcome);
        followUp.RecordEvidence(EvidenceSource.Manual, FollowUpObservation.ReplySeen, null, Now);
        Assert.Equal(FollowUpOutcome.ReplySeen, followUp.Outcome);
        followUp.RecordEvidence(EvidenceSource.Manual, FollowUpObservation.NoReplySeen, null, Now.AddMinutes(-1));
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable, followUp.Outcome);
        Assert.Equal([1L, 2L, 3L], followUp.Evidence.Select(entry => entry.Sequence));
    }

    [Fact]
    public void UnknownSourcesAndObservationsAreNeverInterpretedAsNoReply()
    {
        var known = new FollowUpEvidence(1, EvidenceSource.Manual, FollowUpObservation.NoReplySeen, "Inbox", Now);
        var unknownSource = new FollowUpEvidence(2, (EvidenceSource)42, FollowUpObservation.NoReplySeen, "Inbox", Now);
        var unknownObservation = new FollowUpEvidence(2, EvidenceSource.Manual, (FollowUpObservation)42, "Inbox", Now);
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable,
            FollowUpOutcomeDerivation.Derive((EvidenceSource)42, [known]));
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable,
            FollowUpOutcomeDerivation.Derive(EvidenceSource.Manual, [known, unknownSource]));
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable,
            FollowUpOutcomeDerivation.Derive(EvidenceSource.Manual, [known, unknownObservation]));
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable,
            FollowUpOutcomeDerivation.Derive(EvidenceSource.Manual, [known, known]));

        Assert.Throws<DomainException>(() => Create((EvidenceSource)42));
        Assert.Throws<DomainException>(() =>
            Create().RecordEvidence((EvidenceSource)42, FollowUpObservation.NoReplySeen, "Inbox", Now));
    }

    [Fact]
    public void DeferralAndDismissalPreserveChangeHistoryAndBlockFurtherWrites()
    {
        var followUp = Create();
        var next = Now.AddDays(1);
        followUp.SetDraft("For review only");
        followUp.Defer(next, Now);
        followUp.Dismiss(Now);

        Assert.Equal(next, followUp.DueAtUtc);
        Assert.Equal(FollowUpStatus.Dismissed, followUp.Status);
        Assert.Equal("For review only", followUp.Draft);
        Assert.Equal([FollowUpChangeKind.Deferred, FollowUpChangeKind.Dismissed],
            followUp.Changes.Select(change => change.Kind));
        Assert.Equal(Now, followUp.Changes[0].PreviousDueAtUtc);
        Assert.Equal(next, followUp.Changes[0].NewDueAtUtc);
        var error = Assert.Throws<DomainException>(() => followUp.Defer(next, Now));
        Assert.Equal(DomainErrorCode.InvalidTransition, error.Code);
        Assert.Throws<DomainException>(() =>
            followUp.RecordEvidence(EvidenceSource.Manual, FollowUpObservation.ReplySeen, null, Now));
    }

    [Fact]
    public void CaptureAndEvidenceEnforceBoundsAndExplicitSource()
    {
        Assert.Throws<DomainException>(() => Create((EvidenceSource)0));
        Assert.Throws<DomainException>(() => FollowUp.Create(
            Guid.NewGuid(), new string('x', DomainLimits.FollowUpTitle + 1), "Condition", Now,
            "Europe/Zurich", null, EvidenceSource.Manual));
        Assert.Throws<DomainException>(() => FollowUp.Create(
            Guid.NewGuid(), "Title", new string('x', DomainLimits.FollowUpCondition + 1), Now,
            "Europe/Zurich", null, EvidenceSource.Manual));
        Assert.Throws<DomainException>(() => FollowUp.Create(
            Guid.NewGuid(), "Title", "Condition", Now, "not/a-zone", null, EvidenceSource.Manual));
        var followUp = Create();
        Assert.Throws<DomainException>(() => followUp.RecordEvidence(
            EvidenceSource.Manual, FollowUpObservation.NoReplySeen,
            new string('x', DomainLimits.FollowUpCheckedScope + 1), Now));
        Assert.Throws<DomainException>(() => followUp.SetDraft(new string('x', DomainLimits.FollowUpDraft + 1)));
        Assert.Empty(followUp.Evidence);
        Assert.Null(followUp.Draft);
    }

    [Fact]
    public void CaptureNormalizesInstantToUtc()
    {
        var local = new DateTimeOffset(2026, 11, 21, 13, 0, 0, TimeSpan.FromHours(1));
        var followUp = FollowUp.Create(Guid.NewGuid(), "Title", "Condition", local,
            "Europe/Zurich", null, EvidenceSource.Manual);

        Assert.Equal(Now, followUp.DueAtUtc);
        Assert.Equal(TimeSpan.Zero, followUp.DueAtUtc.Offset);
        Assert.Equal("Europe/Zurich", followUp.TimeZoneId);
    }

    private static FollowUp Create(EvidenceSource source = EvidenceSource.Manual) =>
        FollowUp.Create(Guid.NewGuid(), "Follow up", "If no reply", Now, "Europe/Zurich", null, source);
}
