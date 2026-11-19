using System.Text;
using System.Text.Json;
using Bujo.Domain;

namespace Bujo.Infrastructure.Persistence;

public sealed class JournalBackupSerializer
{
    public string Serialize(JournalBackupSnapshot backup)
    {
        if (!ValidEnvelope(backup))
        {
            throw new JsonException("The journal backup is invalid.");
        }

        var json = JournalStateSerializer.SerializeJournalBackup(backup);
        if (Encoding.UTF8.GetByteCount(json) > DomainLimits.JournalBackupBytes)
        {
            throw new JsonException("The journal backup is too large.");
        }

        return json;
    }

    public JournalBackupSnapshot Deserialize(string backup)
    {
        if (Encoding.UTF8.GetByteCount(backup) > DomainLimits.JournalBackupBytes)
        {
            throw new JsonException("The journal backup is too large.");
        }

        var value = JournalStateSerializer.DeserializeJournalBackup(backup);
        return ValidEnvelope(value)
            ? value
            : throw new JsonException("The journal backup is invalid.");
    }

    private static bool ValidEnvelope(JournalBackupSnapshot? backup) =>
        backup is not null &&
        backup.SchemaVersion is 0 or 1 &&
        backup.Items is not null &&
        backup.Lists is not null &&
        backup.Occurrences is not null &&
        backup.History is not null &&
        (backup.Knowledge is null ||
         backup.Knowledge.Entries is not null &&
         backup.Knowledge.Entries.Count <= DomainLimits.KnowledgeBackupEntries);
}
