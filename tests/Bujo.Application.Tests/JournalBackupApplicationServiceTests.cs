using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Application.Tests;

public sealed class JournalBackupApplicationServiceTests
{
    [Theory]
    [InlineData(" Focus")]
    [InlineData("Ｆｏｃｕｓ")]
    public void RestoreRejectsNormalizedDuplicateListNamesWithoutReplacingEitherStore(string duplicate)
    {
        var (backupService, journal, knowledge) = CreateService();
        Assert.True(journal.CreateList("Existing", JournalListKind.Custom).IsSuccess);
        Assert.True(knowledge.Create("Existing knowledge").IsSuccess);
        var before = backupService.ExportBackup();
        var template = before.Lists.Single(list => list.Name == "Existing");
        var invalid = before with
        {
            Lists = [.. before.Lists, template with { Id = Guid.NewGuid(), Name = "Focus" },
                template with { Id = Guid.NewGuid(), Name = duplicate }],
            Knowledge = before.Knowledge! with { Entries = [] }
        };

        var result = backupService.RestoreBackup(invalid);

        Assert.False(result.IsSuccess);
        Assert.Equal(DomainErrorCode.InvalidInput, result.Error!.Code);
        var after = backupService.ExportBackup();
        Assert.Equal(before.Lists.OrderBy(list => list.Id), after.Lists.OrderBy(list => list.Id));
        Assert.Equal(before.Knowledge!.Entries, after.Knowledge!.Entries);
    }

    [Fact]
    public void RestorePreservesVerbatimLegacyListNameAndPreventsNormalizedDuplicateCreation()
    {
        var (backupService, journal, _) = CreateService();
        Assert.True(journal.CreateList("Legacy", JournalListKind.Weekly).IsSuccess);
        var backup = backupService.ExportBackup();
        var legacy = backup.Lists.Single(list => list.Name == "Legacy");
        backup = backup with { SchemaVersion = 0, Knowledge = null, Lists = backup.Lists.Select(list =>
            list.Id == legacy.Id ? list with { Name = " Legacy " } : list).ToArray() };

        Assert.True(backupService.RestoreBackup(backup).IsSuccess);
        Assert.Equal(" Legacy ", journal.GetSnapshot().Lists.Single(list => list.Id == legacy.Id).Name);
        Assert.Equal(DomainErrorCode.DuplicateListName, journal.CreateList("Legacy", JournalListKind.Custom).Error!.Code);
    }

    [Fact]
    public void CalendarKeysRoundTripAndInvalidKeyKindDoesNotReplaceExistingData()
    {
        var (service, journal, _) = CreateService();
        var key = new JournalPeriodKey(JournalListKind.Monthly, 2024, 2);
        var list = journal.CreateList("February", JournalListKind.Monthly, periodKey: key);
        Assert.True(list.IsSuccess, list.Error?.Message);
        var weekly = journal.CreateList("Weekly", JournalListKind.Weekly, weekKey: new IsoWeekKey(2020, 53));
        Assert.True(weekly.IsSuccess);
        var serializer = new JournalBackupSerializer();
        var backup = serializer.Deserialize(serializer.Serialize(service.ExportBackup()));
        Assert.Equal(key, backup.Lists.Single(item => item.Id == list.Value!.Id).PeriodKey);
        Assert.Equal(new IsoWeekKey(2020, 53), backup.Lists.Single(item => item.Id == weekly.Value!.Id).WeekKey);
        Assert.True(service.RestoreBackup(backup).IsSuccess);
        Assert.Equal(key, journal.GetSnapshot().Lists.Single(item => item.Id == list.Value!.Id).PeriodKey);
        var invalidWeek = backup with { Lists = backup.Lists.Select(item =>
            item.Id == weekly.Value!.Id ? item with { Kind = JournalListKind.Custom } : item).ToArray() };
        Assert.False(service.RestoreBackup(invalidWeek).IsSuccess);

        var invalid = backup with { Lists = backup.Lists.Select(item =>
            item.Id == list.Value!.Id ? item with { Kind = JournalListKind.Custom } : item).ToArray() };
        Assert.False(service.RestoreBackup(invalid).IsSuccess);
        Assert.Equal(key, journal.GetSnapshot().Lists.Single(item => item.Id == list.Value!.Id).PeriodKey);
    }

    [Fact]
    public void BackupRestoreAtomicallyPreservesJournalAndKnowledgeIdentityAndText()
    {
        var (service, journal, knowledge) = CreateService();
        var captured = journal.Capture(
            "Original item",
            "Keep “exactly”\nthis text",
            ["project"],
            PlannedPeriod.Unplanned,
            null);
        var entry = knowledge.Create(
            "A quoted passage: “verbatim”\nSecond line.",
            "Reference",
            "https://example.invalid/source",
            ["ideas"]);
        Assert.True(captured.IsSuccess, captured.Error?.Message);
        Assert.True(entry.IsSuccess, entry.Error?.Message);
        var backup = service.ExportBackup();

        Assert.True(journal.CreateList("Temporary list", JournalListKind.Custom).IsSuccess);
        Assert.True(knowledge.Update(
            entry.Value!.Id,
            "Changed",
            null,
            null,
            [],
            entry.Value.Version).IsSuccess);

        var restored = service.RestoreBackup(backup);

        Assert.True(restored.IsSuccess, restored.Error?.Message);
        var journalItem = Assert.Single(journal.GetSnapshot().Items);
        Assert.Equal(captured.Value!.Item.Id, journalItem.Id);
        Assert.Equal("Keep “exactly”\nthis text", journalItem.Description);
        Assert.Equal(["project"], journalItem.Tags.Select(tag => tag.Value));
        var restoredEntry = knowledge.Get(entry.Value.Id);
        Assert.True(restoredEntry.IsSuccess, restoredEntry.Error?.Message);
        Assert.Equal(entry.Value.Id, restoredEntry.Value!.Id);
        Assert.Equal(entry.Value.Content, restoredEntry.Value.Content);
        Assert.Equal("https://example.invalid/source", restoredEntry.Value.SourceReference);
    }

    [Fact]
    public void InvalidWholeBackupDoesNotReplaceJournalOrKnowledge()
    {
        var (service, journal, knowledge) = CreateService();
        var entry = knowledge.Create("Existing knowledge");
        Assert.True(entry.IsSuccess, entry.Error?.Message);
        var backup = service.ExportBackup();
        var journalChanged = backup with
        {
            Lists =
            [
                .. backup.Lists,
                backup.Lists[0] with { Id = Guid.NewGuid(), Name = "Replacement future log" }
            ],
            Knowledge = backup.Knowledge! with
            {
                Entries = [backup.Knowledge.Entries[0], backup.Knowledge.Entries[0]]
            }
        };

        var restored = service.RestoreBackup(journalChanged);

        Assert.False(restored.IsSuccess);
        Assert.Single(journal.GetSnapshot().Lists);
        Assert.Equal("Existing knowledge", knowledge.Get(entry.Value!.Id).Value!.Content);
    }

    [Fact]
    public void LegacyBackupWithoutKnowledgeLeavesCurrentKnowledgeIntact()
    {
        var (service, journal, knowledge) = CreateService();
        var entry = knowledge.Create("Keep current knowledge");
        Assert.True(entry.IsSuccess, entry.Error?.Message);
        var backup = service.ExportBackup() with { SchemaVersion = 0, Knowledge = null };

        Assert.True(journal.CreateList("Temporary", JournalListKind.Custom).IsSuccess);
        var restored = service.RestoreBackup(backup);

        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Single(journal.GetSnapshot().Lists);
        Assert.Equal("Keep current knowledge", knowledge.Get(entry.Value!.Id).Value!.Content);
    }

    private static (
        JournalBackupApplicationService Backup,
        JournalApplicationService Journal,
        KnowledgeApplicationService Knowledge) CreateService()
    {
        var journalRepository = InMemoryJournalRepository.CreateWithFutureLog(
            Guid.Parse("00000000-0000-0000-0000-000000000101"));
        var knowledgeRepository = new InMemoryKnowledgeRepository();
        return (
            new JournalBackupApplicationService(
                new InMemoryJournalBackupRepository(journalRepository, knowledgeRepository)),
            new JournalApplicationService(new JournalService(journalRepository, new FixedTimeProvider())),
            new KnowledgeApplicationService(knowledgeRepository, new FixedTimeProvider()));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    }
}
