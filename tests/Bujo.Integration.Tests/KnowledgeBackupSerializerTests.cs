using System.Text.Json;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Integration.Tests;

public sealed class KnowledgeBackupSerializerTests
{
    [Fact]
    public void ExportRejectsSnapshotsOutsideTheRestoreEntryLimit()
    {
        var entry = new KnowledgeBackupEntry(
            Guid.NewGuid(),
            "Verbatim content",
            null,
            null,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            1);
        var backup = new KnowledgeBackupSnapshot(
            1,
            Enumerable.Repeat(entry, DomainLimits.KnowledgeBackupEntries + 1).ToArray());

        Assert.Throws<JsonException>(() => new KnowledgeBackupSerializer().Serialize(backup));
    }

    [Fact]
    public void JournalBackupDeserializerAcceptsLegacySnapshotWithoutVersionOrKnowledge()
    {
        var serializer = new JournalBackupSerializer();
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var capture = repository.Capture(new CaptureCommand(
            "Legacy item",
            null,
            ["legacy-tag"],
            PlannedPeriod.Unplanned,
            repository.GetSnapshot().Lists[0].Id,
            0,
            DateTimeOffset.UtcNow));
        Assert.True(capture.IsSuccess, capture.Error?.Message);
        var journal = repository.GetSnapshot();
        var json = JsonSerializer.Serialize(new
        {
            journal.Items,
            journal.Lists,
            journal.Occurrences,
            journal.History
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var backup = serializer.Deserialize(json);

        Assert.Equal(0, backup.SchemaVersion);
        Assert.Null(backup.Knowledge);
        Assert.Equal(journal.Lists, backup.Lists);
        Assert.Equal("legacy-tag", Assert.Single(backup.Items[0].Tags).Value);
    }
}
