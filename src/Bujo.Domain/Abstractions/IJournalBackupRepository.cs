namespace Bujo.Domain;

public interface IJournalBackupRepository
{
    JournalBackupSnapshot ExportBackup();

    JournalBackupSnapshot ExportJournalWithFollowUps();

    DomainResult<JournalBackupSnapshot> RestoreBackup(JournalBackupSnapshot backup);
}
