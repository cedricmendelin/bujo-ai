using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bujo.Application.Tests;

public sealed class FollowUpApplicationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DueListUsesClockAndExplicitActionsPreserveDraftAndHistory()
    {
        var (journal, followUps, _) = CreateServices();
        var linked = journal.Capture("Linked", null, null, null, null).Value!.Item.Id;
        var first = followUps.Capture("Due", "If no reply", Now, "Europe/Zurich", linked, EvidenceSource.Manual);
        var later = followUps.Capture("Later", "If no reply", Now.AddMinutes(1), "Europe/Zurich", null, EvidenceSource.Manual);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(later.IsSuccess, later.Error?.Message);
        var id = first.Value!.Id;
        Assert.Equal(id, Assert.Single(followUps.ListDue().Value!).Id);
        Assert.Equal(FollowUpOutcome.EvidenceUnavailable, first.Value.Outcome);
        Assert.True(followUps.RecordEvidence(id, EvidenceSource.Manual, FollowUpObservation.NoReplySeen, "Inbox").IsSuccess);
        Assert.Equal(FollowUpOutcome.NoReplySeenInCheckedScope, followUps.Get(id).Value!.Outcome);
        Assert.True(followUps.RecordEvidence(id, EvidenceSource.Manual, FollowUpObservation.ReplySeen, null).IsSuccess);
        Assert.True(followUps.SetDraft(id, "Draft only").IsSuccess);
        Assert.True(followUps.Defer(id, Now.AddDays(1)).IsSuccess);
        Assert.Empty(followUps.ListDue().Value!);
        Assert.Equal("Draft only", followUps.Get(id).Value!.Draft);
        Assert.Equal(FollowUpOutcome.ReplySeen, followUps.Get(id).Value!.Outcome);
        Assert.True(followUps.Dismiss(id).IsSuccess);
        Assert.Equal(FollowUpStatus.Dismissed, followUps.Get(id).Value!.Status);
        Assert.False(followUps.SetDraft(id, "not allowed").IsSuccess);
        Assert.False(followUps.Capture("Bad", "Condition", Now, "Europe/Zurich", Guid.NewGuid(), EvidenceSource.Manual).IsSuccess);
    }

    [Fact]
    public void ConcurrentCorrectionsUseUniqueAppendOrderEvenWithIdenticalTimestamps()
    {
        var (_, followUps, _) = CreateServices();
        var id = followUps.Capture("Due", "Condition", Now, "Etc/UTC", null, EvidenceSource.Manual).Value!.Id;
        var results = new DomainResult<FollowUpSnapshot>[100];
        Parallel.For(0, results.Length, index =>
            results[index] = followUps.RecordEvidence(id, EvidenceSource.Manual,
                index % 2 == 0 ? FollowUpObservation.ReplySeen : FollowUpObservation.NoReplySeen, "Inbox"));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        var snapshot = followUps.Get(id).Value!;
        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value), snapshot.Evidence.Select(entry => entry.Sequence));
        var last = snapshot.Evidence[^1];
        Assert.Equal(last.Observation == FollowUpObservation.ReplySeen
            ? FollowUpOutcome.ReplySeen : FollowUpOutcome.NoReplySeenInCheckedScope, snapshot.Outcome);
    }

    [Fact]
    public void DraftCompareAndSetRejectsStaleConcurrentWrites()
    {
        var (_, followUps, _) = CreateServices();
        var id = followUps.Capture("Due", "Condition", Now, "Etc/UTC", null, EvidenceSource.Manual).Value!.Id;
        var results = new DomainResult<FollowUpSnapshot>[2];
        Parallel.For(0, 2, index => results[index] = followUps.SetDraftIfCurrent(id, null, $"Draft {index}"));
        Assert.Single(results, item => item.IsSuccess);
        Assert.Equal(DomainErrorCode.ConcurrencyConflict, Assert.Single(results, item => !item.IsSuccess).Error!.Code);
        var winningDraft = followUps.Get(id).Value!.Draft;
        Assert.True(followUps.SetDraftIfCurrent(id, winningDraft, null).IsSuccess);
        Assert.Null(followUps.Get(id).Value!.Draft);
    }

    [Fact]
    public void BackupRoundTripAndLegacyRestorePreserveReferencesOrFailAtomically()
    {
        var (journal, followUps, backupService) = CreateServices();
        var beforeItem = backupService.ExportBackup();
        var item = journal.Capture("Linked", null, null, null, null).Value!.Item.Id;
        var id = followUps.Capture("Due", "Condition", Now, "Etc/UTC", item, EvidenceSource.Manual).Value!.Id;
        Assert.True(followUps.RecordEvidence(id, EvidenceSource.Manual, FollowUpObservation.NoReplySeen, "Inbox").IsSuccess);
        Assert.True(followUps.Defer(id, Now.AddDays(1)).IsSuccess);
        var serializer = new JournalBackupSerializer();
        var backup = serializer.Deserialize(serializer.Serialize(backupService.ExportBackup()));
        Assert.True(followUps.Dismiss(id).IsSuccess);
        var restored = backupService.RestoreBackup(backup);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        var restoredItem = followUps.Get(id).Value!;
        Assert.Equal(item, restoredItem.ItemId);
        Assert.Equal(FollowUpStatus.Pending, restoredItem.Status);
        Assert.Equal("Inbox", Assert.Single(restoredItem.Evidence).CheckedScope);
        Assert.Single(restoredItem.Changes);

        var legacyWithoutItem = beforeItem with { FollowUps = null };
        var failed = backupService.RestoreBackup(legacyWithoutItem);
        Assert.False(failed.IsSuccess);
        Assert.Equal(item, followUps.Get(id).Value!.ItemId);
        Assert.Contains(journal.GetSnapshot().Items, row => row.Id == item);
        var legacyWithItem = backup with { FollowUps = null };
        Assert.True(backupService.RestoreBackup(legacyWithItem).IsSuccess);
        Assert.Equal(item, followUps.Get(id).Value!.ItemId);
    }

    [Fact]
    public void InvalidBackupAndDirectRestoreCannotOrphanReferences()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var journal = new JournalApplicationService(new JournalService(repository));
        var followUps = new FollowUpApplicationService(new InMemoryFollowUpRepository(repository), new FixedTimeProvider());
        var backups = new JournalBackupApplicationService(new InMemoryJournalBackupRepository(repository, new InMemoryKnowledgeRepository()));
        var empty = repository.GetSnapshot();
        var item = journal.Capture("Linked", null, null, null, null).Value!.Item.Id;
        var id = followUps.Capture("Due", "Condition", Now, "Etc/UTC", item, EvidenceSource.Manual).Value!.Id;
        var backup = backups.ExportBackup();
        var invalid = backup with { FollowUps = backup.FollowUps! with {
            FollowUps = [backup.FollowUps.FollowUps[0] with { ItemId = Guid.NewGuid() }] } };
        Assert.False(backups.RestoreBackup(invalid).IsSuccess);
        Assert.False(repository.RestoreSnapshot(empty).IsSuccess);
        Assert.Equal(item, followUps.Get(id).Value!.ItemId);
        Assert.Contains(repository.GetSnapshot().Items, row => row.Id == item);
    }

    [Fact]
    public void LogicalExportContainsFollowUpsAndAcceptsLegacyJsonWithoutSection()
    {
        var (_, followUps, backups) = CreateServices();
        var id = followUps.Capture("Due", "Condition", Now, "Etc/UTC", null, EvidenceSource.Manual).Value!.Id;
        Assert.True(followUps.RecordEvidence(id, EvidenceSource.Manual, FollowUpObservation.NoReplySeen, "Inbox").IsSuccess);
        var export = new JournalExport(new FixedTimeProvider()).Create(backups.ExportBackup());
        var entry = Assert.Single(export.FollowUps!);
        Assert.Equal(id, entry.Id);
        Assert.Equal(FollowUpOutcome.NoReplySeenInCheckedScope, entry.Outcome);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(export, options)!;
        json.AsObject().Remove("followUps");
        var legacy = JsonSerializer.Deserialize<JournalExportDocument>(json.ToJsonString(), options)!;
        Assert.Null(legacy.FollowUps);
        Assert.NotNull(legacy.Items);
    }

    [Fact]
    public void LogicalExportDoesNotLoadKnowledgeBackup()
    {
        var journal = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var followUps = new FollowUpApplicationService(new InMemoryFollowUpRepository(journal), new FixedTimeProvider());
        var id = followUps.Capture("Due", "Condition", Now, "Etc/UTC", null, EvidenceSource.Manual).Value!.Id;
        var backups = new JournalBackupApplicationService(new InMemoryJournalBackupRepository(journal, new KnowledgeMustNotBeRead()));
        var export = new JournalExport(new FixedTimeProvider()).Create(backups.ExportJournalWithFollowUps());
        Assert.Equal(id, Assert.Single(export.FollowUps!).Id);
        Assert.Empty(export.KnowledgeEntries);
        Assert.Throws<InvalidOperationException>(() => backups.ExportBackup());
    }

    private static (JournalApplicationService Journal, FollowUpApplicationService FollowUps, JournalBackupApplicationService Backup) CreateServices()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        return (new JournalApplicationService(new JournalService(repository)),
            new FollowUpApplicationService(new InMemoryFollowUpRepository(repository), new FixedTimeProvider()),
            new JournalBackupApplicationService(new InMemoryJournalBackupRepository(repository, new InMemoryKnowledgeRepository())));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class KnowledgeMustNotBeRead : IKnowledgeRepository
    {
        public KnowledgeBackupSnapshot ExportBackup() => throw new InvalidOperationException("Knowledge was accessed.");
        public DomainResult<KnowledgeEntrySnapshot> Create(CreateKnowledgeEntryCommand command) => throw new NotImplementedException();
        public DomainResult<KnowledgeEntrySnapshot> Get(Guid id) => throw new NotImplementedException();
        public DomainResult<KnowledgeEntrySnapshot> Update(UpdateKnowledgeEntryCommand command) => throw new NotImplementedException();
        public DomainResult<KnowledgeEntrySnapshot> SetArchived(Guid id, bool archived, long expectedVersion, DateTimeOffset updatedAt) => throw new NotImplementedException();
        public DomainResult<KnowledgeEntrySnapshot> Delete(Guid id, long expectedVersion) => throw new NotImplementedException();
        public DomainResult<KnowledgeSearchPage> Search(KnowledgeSearchCriteria criteria) => throw new NotImplementedException();
        public IReadOnlyList<KnowledgeTagInfo> GetTags() => throw new NotImplementedException();
        public KnowledgeIndexState GetIndexState() => throw new NotImplementedException();
        public DomainResult<KnowledgeBackupSnapshot> RestoreBackup(KnowledgeBackupSnapshot backup) => throw new NotImplementedException();
    }
}
