using Bujo.Domain;

namespace Bujo.Application;

public sealed class JournalBackupApplicationService(IJournalBackupRepository repository)
{
    public JournalBackupSnapshot ExportBackup() => repository.ExportBackup();

    public JournalBackupSnapshot ExportJournalWithFollowUps() => repository.ExportJournalWithFollowUps();

    public DomainResult<JournalBackupSnapshot> RestoreBackup(JournalBackupSnapshot backup) =>
        repository.RestoreBackup(backup);
}
