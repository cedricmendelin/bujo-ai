using System.Data;
using Bujo.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Infrastructure.Persistence;

public sealed class EfJournalBackupRepository(IDbContextFactory<JournalDbContext> contextFactory)
    : IJournalBackupRepository
{
    public JournalBackupSnapshot ExportBackup()
    {
        using var context = contextFactory.CreateDbContext();
        using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
        var backup = Export(context);
        transaction.Commit();
        return backup;
    }

    public DomainResult<JournalBackupSnapshot> RestoreBackup(JournalBackupSnapshot backup)
    {
        var validated = JournalBackupValidator.Validate(backup);
        if (!validated.IsSuccess)
        {
            return DomainResult<JournalBackupSnapshot>.Failure(validated.Error!);
        }

        try
        {
            using var strategyContext = contextFactory.CreateDbContext();
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            return strategy.Execute(() =>
            {
                using var context = contextFactory.CreateDbContext();
                using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
                EfJournalRepository.ReplaceJournal(context, validated.Value!.Journal);
                if (validated.Value.Knowledge is not null)
                {
                    context.KnowledgeEntries.ExecuteDelete();
                    context.KnowledgeEntries.AddRange(validated.Value.Knowledge.Entries.Select(
                        KnowledgeEntry.FromBackup).Select(
                        entry => EfKnowledgeRepository.ToEntity(entry.ToSnapshot())));
                }

                context.SaveChanges();
                transaction.Commit();
                return DomainResult<JournalBackupSnapshot>.Success(Export(context));
            });
        }
        catch (DbUpdateException)
        {
            return DomainResult<JournalBackupSnapshot>.Failure(new DomainError(
                DomainErrorCode.InvariantViolation,
                "The journal backup could not be restored."));
        }
    }

    private static JournalBackupSnapshot Export(JournalDbContext context)
    {
        var snapshot = EfJournalRepository.LoadSnapshot(context, tracked: false);
        return From(snapshot, LoadKnowledge(context));
    }

    private static KnowledgeBackupSnapshot LoadKnowledge(JournalDbContext context) =>
        new(1, context.KnowledgeEntries.AsNoTracking()
            .Include(entry => entry.Tags)
            .OrderBy(entry => entry.Id)
            .ToArray()
            .Select(EfKnowledgeRepository.ToSnapshot)
            .Select(ToBackupEntry)
            .ToArray());

    private static JournalBackupSnapshot From(JournalSnapshot journal, KnowledgeBackupSnapshot knowledge) =>
        new(1, journal.Items, journal.Lists, journal.Occurrences, journal.History, knowledge);

    private static KnowledgeBackupEntry ToBackupEntry(KnowledgeEntrySnapshot entry) =>
        new(
            entry.Id,
            entry.Content,
            entry.Title,
            entry.SourceReference,
            entry.Tags.Select(tag => tag.Value).ToArray(),
            entry.CreatedAt,
            entry.UpdatedAt,
            entry.ArchivedAt,
            entry.Version);

}

public sealed class InMemoryJournalBackupRepository(
    IJournalRepository journal,
    IKnowledgeRepository knowledge) : IJournalBackupRepository
{
    public JournalBackupSnapshot ExportBackup()
    {
        var snapshot = journal.GetSnapshot();
        return new JournalBackupSnapshot(
            1,
            snapshot.Items,
            snapshot.Lists,
            snapshot.Occurrences,
            snapshot.History,
            knowledge.ExportBackup());
    }

    public DomainResult<JournalBackupSnapshot> RestoreBackup(JournalBackupSnapshot backup)
    {
        var validated = JournalBackupValidator.Validate(backup);
        if (!validated.IsSuccess)
        {
            return DomainResult<JournalBackupSnapshot>.Failure(validated.Error!);
        }

        var journalResult = journal.RestoreSnapshot(validated.Value!.Journal);
        if (!journalResult.IsSuccess)
        {
            return DomainResult<JournalBackupSnapshot>.Failure(journalResult.Error!);
        }

        if (validated.Value.Knowledge is not null)
        {
            var knowledgeResult = knowledge.RestoreBackup(validated.Value.Knowledge);
            if (!knowledgeResult.IsSuccess)
            {
                throw new InvalidOperationException(
                    "Validated knowledge backup unexpectedly failed during in-memory restore.");
            }
        }

        return DomainResult<JournalBackupSnapshot>.Success(ExportBackup());
    }
}

internal static class JournalBackupValidator
{
    public static DomainResult<ValidatedJournalBackup> Validate(JournalBackupSnapshot backup)
    {
        if (backup is null ||
            backup.SchemaVersion is not (0 or 1) ||
            backup.Items is null || backup.Lists is null ||
            backup.Occurrences is null || backup.History is null)
        {
            return Invalid();
        }

        try
        {
            var snapshot = backup.ToJournalSnapshot();
            InMemoryJournalRepository.ValidateSnapshot(snapshot);
            if (backup.Knowledge is not null)
            {
                var result = new InMemoryKnowledgeRepository().RestoreBackup(backup.Knowledge);
                if (!result.IsSuccess)
                {
                    return DomainResult<ValidatedJournalBackup>.Failure(result.Error!);
                }
            }

            return DomainResult<ValidatedJournalBackup>.Success(
                new ValidatedJournalBackup(snapshot, backup.Knowledge));
        }
        catch (Exception exception) when (
            exception is ArgumentException or DomainException or InvalidOperationException)
        {
            return Invalid();
        }
    }

    private static DomainResult<ValidatedJournalBackup> Invalid() =>
        DomainResult<ValidatedJournalBackup>.Failure(new DomainError(
            DomainErrorCode.InvalidInput,
            "The journal backup is invalid."));
}

internal sealed record ValidatedJournalBackup(
    JournalSnapshot Journal,
    KnowledgeBackupSnapshot? Knowledge);
