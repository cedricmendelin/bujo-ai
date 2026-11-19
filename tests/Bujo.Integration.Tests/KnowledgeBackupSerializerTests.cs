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
}
