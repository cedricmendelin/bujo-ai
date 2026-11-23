using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit.Sdk;

namespace Bujo.Integration.Tests;

[CollectionDefinition("Shared SQL Server database", DisableParallelization = true)]
public sealed class SharedSqlServerDatabaseCollection
{
}

[Collection("Shared SQL Server database")]
public sealed class FollowUpPersistenceTests
{
    [Fact]
    public void MigrationAddsOnlyFollowUpTablesAndRestrictsItemDeletion()
    {
        var options = new DbContextOptionsBuilder<JournalDbContext>().UseSqlServer().Options;
        using var context = new JournalDbContext(options);
        var sql = context.GetService<IMigrator>().GenerateScript(
            "20261119215100_AddKnowledgeFullText", "20261121220000_AddFollowUps");
        Assert.Contains("CREATE TABLE [FollowUps]", sql);
        Assert.Contains("CREATE TABLE [FollowUpEvidence]", sql);
        Assert.Contains("CREATE TABLE [FollowUpChanges]", sql);
        Assert.Contains("FOREIGN KEY ([ItemId]) REFERENCES [Items] ([Id]) ON DELETE NO ACTION", sql);
        Assert.DoesNotContain("ALTER TABLE [Items]", sql);
    }

    [Fact]
    public void SqlMigrationConcurrentEvidenceAndRestoreRoundTrip()
    {
        var connection = Environment.GetEnvironmentVariable("BUJO_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            throw SkipException.ForSkip("Set BUJO_TEST_SQL_CONNECTION to a disposable SQL Server database.");

        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).Options;
        var factory = new Factory(options);
        Guid legacyItemId;
        using (var context = new JournalDbContext(options))
        {
            var migrator = context.GetService<IMigrator>();
            migrator.Migrate("20261119215100_AddKnowledgeFullText");
            var oldJournal = new JournalApplicationService(new JournalService(new EfJournalRepository(factory)));
            legacyItemId = oldJournal.Capture("Before follow-up migration", null, null, null, null).Value!.Item.Id;
            migrator.Migrate();
        }

        var journalRepository = new EfJournalRepository(factory);
        Assert.Contains(journalRepository.GetSnapshot().Items, row => row.Id == legacyItemId);
        var journal = new JournalApplicationService(new JournalService(journalRepository));
        var followUps = new FollowUpApplicationService(new EfFollowUpRepository(factory));
        var backups = new JournalBackupApplicationService(new EfJournalBackupRepository(factory));
        var beforeItem = backups.ExportBackup();
        var item = journal.Capture("SQL linked", null, null, null, null).Value!.Item.Id;
        var id = followUps.Capture("Due", "Condition", DateTimeOffset.UtcNow, "Etc/UTC", item, EvidenceSource.Manual).Value!.Id;

        var results = new DomainResult<FollowUpSnapshot>[12];
        Parallel.For(0, results.Length, index =>
            results[index] = followUps.RecordEvidence(id, EvidenceSource.Manual,
                index % 2 == 0 ? FollowUpObservation.ReplySeen : FollowUpObservation.NoReplySeen, "Inbox"));
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        var recorded = followUps.Get(id).Value!;
        Assert.Equal(Enumerable.Range(1, 12).Select(value => (long)value), recorded.Evidence.Select(entry => entry.Sequence));
        var drafts = new DomainResult<FollowUpSnapshot>[2];
        Parallel.For(0, 2, index => drafts[index] = followUps.SetDraftIfCurrent(id, null, $"Draft {index}"));
        Assert.Single(drafts, result => result.IsSuccess);
        Assert.Equal(DomainErrorCode.ConcurrencyConflict, Assert.Single(drafts, result => !result.IsSuccess).Error!.Code);

        var serializer = new JournalBackupSerializer();
        var backup = serializer.Deserialize(serializer.Serialize(backups.ExportBackup()));
        Assert.True(followUps.Dismiss(id).IsSuccess);
        var restored = backups.RestoreBackup(backup);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Equal(FollowUpStatus.Pending, followUps.Get(id).Value!.Status);
        Assert.Equal(12, followUps.Get(id).Value!.Evidence.Count);
        var invalid = backup with
        {
            FollowUps = backup.FollowUps! with
            {
                FollowUps = [backup.FollowUps.FollowUps[0] with { ItemId = Guid.NewGuid() }]
            }
        };
        Assert.False(backups.RestoreBackup(invalid).IsSuccess);
        Assert.Equal(12, followUps.Get(id).Value!.Evidence.Count);
        Assert.False(backups.RestoreBackup(beforeItem with { FollowUps = null }).IsSuccess);
        Assert.False(journalRepository.RestoreSnapshot(beforeItem.ToJournalSnapshot()).IsSuccess);
        Assert.Equal(item, followUps.Get(id).Value!.ItemId);
        Assert.True(backups.RestoreBackup(backup with { FollowUps = null }).IsSuccess);
        Assert.Equal(item, followUps.Get(id).Value!.ItemId);

        var oldBackup = backups.ExportBackup() with { FollowUps = null };
        var raceItem = journal.Capture("Restore race", null, null, null, null).Value!.Item.Id;
        DomainResult<FollowUpSnapshot>? captureRace = null;
        DomainResult<JournalBackupSnapshot>? restoreRace = null;
        Parallel.Invoke(
            () => captureRace = followUps.Capture("Race", "Condition", DateTimeOffset.UtcNow, "Etc/UTC", raceItem, EvidenceSource.Manual),
            () => restoreRace = backups.RestoreBackup(oldBackup));
        Assert.NotNull(captureRace);
        Assert.NotNull(restoreRace);
        Assert.NotEqual(captureRace.IsSuccess, restoreRace.IsSuccess);
        if (captureRace.IsSuccess)
            Assert.Contains(journal.GetSnapshot().Items, row => row.Id == raceItem);
        else
            Assert.DoesNotContain(journal.GetSnapshot().Items, row => row.Id == raceItem);
    }

    private sealed class Factory(DbContextOptions<JournalDbContext> options) : IDbContextFactory<JournalDbContext>
    {
        public JournalDbContext CreateDbContext() => new(options);
    }
}
