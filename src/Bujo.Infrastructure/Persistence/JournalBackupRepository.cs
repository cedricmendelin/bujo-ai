using System.Data;
using Bujo.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Infrastructure.Persistence;

public sealed class EfJournalBackupRepository(IDbContextFactory<JournalDbContext> contextFactory)
    : IJournalBackupRepository
{
    public JournalBackupSnapshot ExportBackup() => ExportWithTransaction(includeKnowledge: true);

    public JournalBackupSnapshot ExportJournalWithFollowUps() => ExportWithTransaction(includeKnowledge: false);

    private JournalBackupSnapshot ExportWithTransaction(bool includeKnowledge)
    {
        using var strategyContext = contextFactory.CreateDbContext();
        return strategyContext.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var context = contextFactory.CreateDbContext();
            using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
            var backup = Export(context, includeKnowledge);
            transaction.Commit();
            return backup;
        });
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
                var followUps = validated.Value!.FollowUps ?? LoadFollowUps(context);
                ReplaceJournalAndFollowUps(context, validated.Value.Journal, followUps);
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
        catch (DomainException exception)
        {
            return DomainResult<JournalBackupSnapshot>.Failure(exception.ToError());
        }
    }

    private static JournalBackupSnapshot Export(JournalDbContext context, bool includeKnowledge = true)
    {
        var snapshot = EfJournalRepository.LoadSnapshot(context, tracked: false);
        return From(snapshot, includeKnowledge ? LoadKnowledge(context) : null, LoadFollowUps(context));
    }

    private static KnowledgeBackupSnapshot LoadKnowledge(JournalDbContext context) =>
        new(1, context.KnowledgeEntries.AsNoTracking()
            .Include(entry => entry.Tags)
            .OrderBy(entry => entry.Id)
            .ToArray()
            .Select(EfKnowledgeRepository.ToSnapshot)
            .Select(ToBackupEntry)
            .ToArray());

    internal static FollowUpBackupSnapshot LoadFollowUps(JournalDbContext context) =>
        new(1, context.FollowUps.AsNoTracking().Include(item => item.Evidence).Include(item => item.Changes)
            .AsSplitQuery().OrderBy(item => item.Id).ToArray().Select(EfFollowUpRepository.ToSnapshot).ToArray());

    internal static void ReplaceJournalAndFollowUps(JournalDbContext context, JournalSnapshot journal,
        FollowUpBackupSnapshot followUps)
    {
        JournalBackupValidator.ValidateFollowUps(followUps, journal);
        // SQL Server cannot defer the item FK. Retain the validated records in
        // memory while replacing items, then reinsert in the same transaction.
        context.FollowUps.ExecuteDelete();
        EfJournalRepository.ReplaceJournal(context, journal);
        context.SaveChanges();
        context.FollowUps.AddRange(followUps.FollowUps.Select(EfFollowUpRepository.ToEntity));
        context.SaveChanges();
    }

    private static JournalBackupSnapshot From(JournalSnapshot journal, KnowledgeBackupSnapshot? knowledge,
        FollowUpBackupSnapshot followUps) =>
        new(1, journal.Items, journal.Lists, journal.Occurrences, journal.History, knowledge, followUps);

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
    public JournalBackupSnapshot ExportBackup() => Export(includeKnowledge: true);

    public JournalBackupSnapshot ExportJournalWithFollowUps() => Export(includeKnowledge: false);

    private JournalBackupSnapshot Export(bool includeKnowledge)
    {
        if (journal is InMemoryJournalRepository inMemory)
        {
            lock (inMemory.SyncRoot)
            {
                var journalSnapshot = journal.GetSnapshot();
                return new JournalBackupSnapshot(1, journalSnapshot.Items, journalSnapshot.Lists,
                    journalSnapshot.Occurrences, journalSnapshot.History, includeKnowledge ? knowledge.ExportBackup() : null,
                    new FollowUpBackupSnapshot(1, inMemory.FollowUps.Values.OrderBy(item => item.Id)
                        .Select(item => item.ToSnapshot()).ToArray()));
            }
        }

        var snapshot = journal.GetSnapshot();
        return new JournalBackupSnapshot(
            1,
            snapshot.Items,
            snapshot.Lists,
            snapshot.Occurrences,
            snapshot.History,
            includeKnowledge ? knowledge.ExportBackup() : null);
    }

    public DomainResult<JournalBackupSnapshot> RestoreBackup(JournalBackupSnapshot backup)
    {
        var validated = JournalBackupValidator.Validate(backup);
        if (!validated.IsSuccess)
        {
            return DomainResult<JournalBackupSnapshot>.Failure(validated.Error!);
        }

        if (journal is InMemoryJournalRepository inMemory)
        {
            lock (inMemory.SyncRoot)
            {
                var retained = inMemory.FollowUps.Values.Select(item => item.ToSnapshot()).ToArray();
                var desired = validated.Value!.FollowUps ?? new FollowUpBackupSnapshot(1, retained);
                try
                {
                    JournalBackupValidator.ValidateFollowUps(desired, validated.Value.Journal);
                }
                catch (DomainException exception)
                {
                    return DomainResult<JournalBackupSnapshot>.Failure(exception.ToError());
                }

                inMemory.FollowUps.Clear();
                var result = journal.RestoreSnapshot(validated.Value.Journal);
                if (!result.IsSuccess)
                {
                    foreach (var item in retained) inMemory.FollowUps.Add(item.Id, FollowUp.FromSnapshot(item));
                    return DomainResult<JournalBackupSnapshot>.Failure(result.Error!);
                }
                foreach (var item in desired.FollowUps) inMemory.FollowUps.Add(item.Id, FollowUp.FromSnapshot(item));
                if (validated.Value.Knowledge is not null)
                {
                    var knowledgeResult = knowledge.RestoreBackup(validated.Value.Knowledge);
                    if (!knowledgeResult.IsSuccess)
                        throw new InvalidOperationException("Validated knowledge backup unexpectedly failed.");
                }
                return DomainResult<JournalBackupSnapshot>.Success(ExportBackup());
            }
        }

        if (validated.Value!.FollowUps is not null)
            return DomainResult<JournalBackupSnapshot>.Failure(new DomainError(
                DomainErrorCode.InvariantViolation, "This repository cannot restore follow-ups."));
        var journalResult = journal.RestoreSnapshot(validated.Value.Journal);
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

            if (backup.FollowUps is not null)
            {
                ValidateFollowUps(backup.FollowUps, snapshot);
            }
            return DomainResult<ValidatedJournalBackup>.Success(
                new ValidatedJournalBackup(snapshot, backup.Knowledge, backup.FollowUps));
        }
        catch (Exception exception) when (
            exception is ArgumentException or DomainException or InvalidOperationException)
        {
            return Invalid();
        }
    }

    internal static void ValidateFollowUps(FollowUpBackupSnapshot backup, JournalSnapshot journal)
    {
        if (backup.SchemaVersion != 1 || backup.FollowUps is null ||
            backup.FollowUps.Count > DomainLimits.FollowUpBackupEntries)
            throw DomainLimits.Invalid("The follow-up backup is invalid.");
        var ids = journal.Items.Select(item => item.Id).ToHashSet();
        var followUpIds = new HashSet<Guid>();
        foreach (var item in backup.FollowUps)
        {
            var aggregate = FollowUp.FromSnapshot(item);
            if (!followUpIds.Add(aggregate.Id) ||
                aggregate.ItemId is { } itemId && !ids.Contains(itemId))
                throw DomainLimits.Invalid("The follow-up backup contains invalid item references or duplicate IDs.");
        }
    }

    private static DomainResult<ValidatedJournalBackup> Invalid() =>
        DomainResult<ValidatedJournalBackup>.Failure(new DomainError(
            DomainErrorCode.InvalidInput,
            "The journal backup is invalid."));
}

internal sealed record ValidatedJournalBackup(
    JournalSnapshot Journal,
    KnowledgeBackupSnapshot? Knowledge,
    FollowUpBackupSnapshot? FollowUps);
