using System.Text.Json;
using System.Text.Json.Nodes;
using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bujo.Integration.Tests;

public sealed class ItemTypePersistenceTests
{
    [Fact]
    public void SnapshotBackupAndExportPreserveThoughtAndReadLegacyPayloadsAsTask()
    {
        var futureId = Guid.NewGuid();
        var repository = InMemoryJournalRepository.CreateWithFutureLog(futureId);
        var captured = repository.Capture(new CaptureCommand(
            "Synthetic thought", null, [], PlannedPeriod.Unplanned, futureId, 0,
            DateTimeOffset.UtcNow, ItemType.Thought));
        Assert.True(captured.IsSuccess);
        var snapshot = repository.GetSnapshot();
        Assert.Equal(ItemType.Thought, Assert.Single(snapshot.Items).Type);
        Assert.Equal(ItemType.Thought, Assert.Single(new InMemoryJournalRepository(snapshot).GetSnapshot().Items).Type);

        var backupRepository = new InMemoryJournalBackupRepository(repository, new InMemoryKnowledgeRepository());
        var serializer = new JournalBackupSerializer();
        var json = serializer.Serialize(backupRepository.ExportBackup());
        var document = JsonNode.Parse(json)!;
        Assert.Equal("Thought", document["items"]![0]!["type"]!.GetValue<string>());
        var roundTrip = serializer.Deserialize(json);
        var restoredRepository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var restoredBackup = new InMemoryJournalBackupRepository(restoredRepository, new InMemoryKnowledgeRepository());
        Assert.True(restoredBackup.RestoreBackup(roundTrip).IsSuccess);
        Assert.Equal(ItemType.Thought, Assert.Single(restoredRepository.GetSnapshot().Items).Type);

        document["items"]![0]!.AsObject().Remove("type");
        var legacy = serializer.Deserialize(document.ToJsonString());
        Assert.Equal(ItemType.Task, Assert.Single(legacy.Items).Type);
        Assert.True(restoredBackup.RestoreBackup(legacy).IsSuccess);
        Assert.Equal(ItemType.Task, Assert.Single(restoredRepository.GetSnapshot().Items).Type);

        var export = new JournalExport(TimeProvider.System).Create(snapshot);
        Assert.Equal("Thought", Assert.Single(export.Items).Type);
        var exportJson = JsonSerializer.SerializeToNode(export, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Thought", exportJson["items"]![0]!["type"]!.GetValue<string>());
        exportJson["items"]![0]!.AsObject().Remove("type");
        var legacyExport = JsonSerializer.Deserialize<JournalExportDocument>(
            exportJson.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Task", Assert.Single(legacyExport.Items).Type);

        var invalid = roundTrip with { Items = [roundTrip.Items[0] with { Type = (ItemType)42 }] };
        Assert.False(restoredBackup.RestoreBackup(invalid).IsSuccess);
    }

    [Fact]
    public void MigrationAddsNonNullIntegerWithPersistentTaskDefault()
    {
        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer()
            .Options;
        using var context = new JournalDbContext(options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20261119120000_AddKnowledgeEntries", "20261119200000_AddItemType");
        Assert.Contains("ALTER TABLE [Items] ADD [Type] int NOT NULL DEFAULT 0", script);
        var property = context.Model.FindEntityType(typeof(ItemEntity))!.FindProperty(nameof(ItemEntity.Type))!;
        Assert.Equal(ItemType.Task, property.GetDefaultValue());
    }

    [Theory]
    [InlineData(OccurrenceAction.Done)]
    [InlineData(OccurrenceAction.Abandon)]
    [InlineData(OccurrenceAction.Reopen)]
    public void ThoughtDoesNotBlockListClosureAndCannotBeTransitioned(OccurrenceAction action)
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var list = repository.CreateList(new CreateListCommand("Synthetic", JournalListKind.Custom)).Value!;
        var thought = repository.Capture(new CaptureCommand(
            "Thought", null, [], PlannedPeriod.Unplanned, list.Id, list.Version, DateTimeOffset.UtcNow, ItemType.Thought)).Value!;
        var updatedList = repository.GetSnapshot().Lists.Single(candidate => candidate.Id == list.Id);
        var transition = repository.Transition(new TransitionCommand(
            thought.Occurrence.Id, action, thought.Item.Version, updatedList.Version, DateTimeOffset.UtcNow));
        Assert.Equal(DomainErrorCode.InvalidTransition, transition.Error!.Code);
        Assert.True(repository.CloseList(new ListVersionCommand(list.Id, updatedList.Version, DateTimeOffset.UtcNow)).IsSuccess);
        Assert.Equal(OccurrenceState.Open, Assert.Single(repository.GetSnapshot().Occurrences).State);
    }

    [Fact]
    public void OpenTaskStillBlocksListClosureAlongsideThought()
    {
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var list = repository.CreateList(new CreateListCommand("Synthetic", JournalListKind.Custom)).Value!;
        var task = repository.Capture(new CaptureCommand(
            "Task", null, [], PlannedPeriod.Unplanned, list.Id, list.Version, DateTimeOffset.UtcNow)).Value!;
        var current = repository.GetSnapshot().Lists.Single(candidate => candidate.Id == list.Id);
        Assert.True(repository.Capture(new CaptureCommand(
            "Thought", null, [], PlannedPeriod.Unplanned, list.Id, current.Version, DateTimeOffset.UtcNow, ItemType.Thought)).IsSuccess);
        current = repository.GetSnapshot().Lists.Single(candidate => candidate.Id == list.Id);
        var result = repository.CloseList(new ListVersionCommand(list.Id, current.Version, DateTimeOffset.UtcNow));
        Assert.Equal(DomainErrorCode.UnresolvedOccurrences, result.Error!.Code);
        Assert.Equal([task.Occurrence.Id], result.Error.RelatedIds);
    }
}
