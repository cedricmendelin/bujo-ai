namespace Bujo.Domain;

public interface IJournalBackupRepository
{
    JournalBackupSnapshot ExportBackup();

    DomainResult<JournalBackupSnapshot> RestoreBackup(JournalBackupSnapshot backup);
}
