using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Application.Tests;

public sealed class JournalBackupApplicationServiceTests
{
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
