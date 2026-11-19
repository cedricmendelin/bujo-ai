using System.Text;
using System.Text.Json;
using Bujo.Domain;

namespace Bujo.Infrastructure.Persistence;

public sealed class KnowledgeBackupSerializer
{
    public string Serialize(KnowledgeBackupSnapshot backup)
    {
        if (backup is null || backup.Entries is null ||
            backup.Entries.Count > DomainLimits.KnowledgeBackupEntries)
        {
            throw new JsonException("The knowledge backup has an invalid entry count.");
        }

        var json = JournalStateSerializer.SerializeKnowledgeBackup(backup);
        if (Encoding.UTF8.GetByteCount(json) > DomainLimits.KnowledgeBackupBytes)
        {
            throw new JsonException("The knowledge backup is too large.");
        }

        return json;
    }

    public KnowledgeBackupSnapshot Deserialize(string backup)
    {
        if (Encoding.UTF8.GetByteCount(backup) > DomainLimits.KnowledgeBackupBytes)
        {
            throw new JsonException("The knowledge backup is too large.");
        }

        return JournalStateSerializer.DeserializeKnowledgeBackup(backup);
    }
}
