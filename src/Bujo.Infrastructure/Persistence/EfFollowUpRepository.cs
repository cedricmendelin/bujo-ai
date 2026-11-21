using System.Data;
using Bujo.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Infrastructure.Persistence;

public sealed class EfFollowUpRepository(IDbContextFactory<JournalDbContext> contextFactory) : IFollowUpRepository
{
    public DomainResult<FollowUpSnapshot> Create(CreateFollowUpCommand command)
    {
        try
        {
            var item = FollowUp.Create(command.Id, command.Title, command.Condition, command.DueAtUtc,
                command.TimeZoneId, command.ItemId, command.Source);
            using var strategyContext = contextFactory.CreateDbContext();
            return strategyContext.Database.CreateExecutionStrategy().Execute(() =>
            {
                using var context = contextFactory.CreateDbContext();
                using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
                if (command.ItemId is { } id && !context.Items.Any(row => row.Id == id))
                    return Failure<FollowUpSnapshot>(DomainErrorCode.NotFound, "The linked item was not found.");
                context.FollowUps.Add(ToEntity(item.ToSnapshot()));
                context.SaveChanges();
                transaction.Commit();
                return DomainResult<FollowUpSnapshot>.Success(item.ToSnapshot());
            });
        }
        catch (DomainException exception)
        {
            return DomainResult<FollowUpSnapshot>.Failure(exception.ToError());
        }
        catch (DbUpdateException)
        {
            return Failure<FollowUpSnapshot>(DomainErrorCode.InvariantViolation, "The follow-up could not be saved.");
        }
    }

    public DomainResult<IReadOnlyList<FollowUpSnapshot>> ListDue(DateTimeOffset nowUtc)
    {
        using var strategyContext = contextFactory.CreateDbContext();
        return strategyContext.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var context = contextFactory.CreateDbContext();
            using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
            IReadOnlyList<FollowUpSnapshot> result = context.FollowUps.AsNoTracking()
                .Where(item => item.Status == FollowUpStatus.Pending && item.DueAtUtc <= nowUtc)
                .Include(item => item.Evidence).Include(item => item.Changes)
                .OrderBy(item => item.DueAtUtc).ThenBy(item => item.Id)
                .AsSplitQuery().ToArray().Select(ToSnapshot).ToArray();
            transaction.Commit();
            return DomainResult<IReadOnlyList<FollowUpSnapshot>>.Success(result);
        });
    }

    public DomainResult<FollowUpSnapshot> Get(Guid id)
    {
        using var strategyContext = contextFactory.CreateDbContext();
        return strategyContext.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var context = contextFactory.CreateDbContext();
            using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
            var entity = context.FollowUps.AsNoTracking().Include(item => item.Evidence)
                .Include(item => item.Changes).AsSplitQuery().SingleOrDefault(item => item.Id == id);
            var result = entity is null
                ? Failure<FollowUpSnapshot>(DomainErrorCode.NotFound, "The follow-up was not found.")
                : DomainResult<FollowUpSnapshot>.Success(ToSnapshot(entity));
            transaction.Commit();
            return result;
        });
    }

    public DomainResult<FollowUpSnapshot> RecordEvidence(Guid id, EvidenceSource source,
        FollowUpObservation observation, string? checkedScope, DateTimeOffset recordedAtUtc) =>
        Mutate(id, item => item.RecordEvidence(source, observation, checkedScope, recordedAtUtc));

    public DomainResult<FollowUpSnapshot> Dismiss(Guid id, DateTimeOffset occurredAtUtc) =>
        Mutate(id, item => item.Dismiss(occurredAtUtc));

    public DomainResult<FollowUpSnapshot> Defer(Guid id, DateTimeOffset dueAtUtc, DateTimeOffset occurredAtUtc) =>
        Mutate(id, item => item.Defer(dueAtUtc, occurredAtUtc));

    public DomainResult<FollowUpSnapshot> SetDraft(Guid id, string? draft) =>
        Mutate(id, item => item.SetDraft(draft));

    public DomainResult<FollowUpSnapshot> SetDraftIfCurrent(Guid id, string? expectedDraft, string? draft) =>
        Mutate(id, item =>
        {
            if (!string.Equals(item.Draft, expectedDraft, StringComparison.Ordinal))
                throw new DomainException(DomainErrorCode.ConcurrencyConflict, "The follow-up draft has changed.");
            item.SetDraft(draft);
        });

    private DomainResult<FollowUpSnapshot> Mutate(Guid id, Action<FollowUp> action)
    {
        try
        {
            using var strategyContext = contextFactory.CreateDbContext();
            return strategyContext.Database.CreateExecutionStrategy().Execute(() =>
            {
                using var context = contextFactory.CreateDbContext();
                using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
                // Acquire a write lock before reading children: concurrent corrections
                // serialize on the aggregate row, not on their client timestamps.
                var entity = context.FollowUps
                    .FromSql($"SELECT * FROM dbo.FollowUps WITH (UPDLOCK, HOLDLOCK) WHERE Id = {id}")
                    .SingleOrDefault();
                if (entity is null)
                    return Failure<FollowUpSnapshot>(DomainErrorCode.NotFound, "The follow-up was not found.");
                context.Entry(entity).Collection(item => item.Evidence).Load();
                context.Entry(entity).Collection(item => item.Changes).Load();
                var aggregate = FollowUp.FromSnapshot(ToSnapshot(entity));
                action(aggregate);
                var updated = aggregate.ToSnapshot();
                entity.DueAtUtc = updated.DueAtUtc;
                entity.Status = updated.Status;
                entity.Draft = updated.Draft;
                entity.Version++;
                foreach (var evidence in updated.Evidence.Skip(entity.Evidence.Count))
                    entity.Evidence.Add(ToEntity(id, evidence));
                foreach (var change in updated.Changes.Skip(entity.Changes.Count))
                    entity.Changes.Add(ToEntity(id, change));
                context.SaveChanges();
                transaction.Commit();
                return DomainResult<FollowUpSnapshot>.Success(updated);
            });
        }
        catch (DomainException exception)
        {
            return DomainResult<FollowUpSnapshot>.Failure(exception.ToError());
        }
        catch (DbUpdateException)
        {
            return Failure<FollowUpSnapshot>(DomainErrorCode.ConcurrencyConflict, "The follow-up could not be updated.");
        }
    }

    internal static FollowUpSnapshot ToSnapshot(FollowUpEntity entity) => new(
        entity.Id, entity.Title, entity.Condition, entity.DueAtUtc, entity.TimeZoneId, entity.ItemId,
        entity.Source, entity.Status, entity.Draft,
        entity.Evidence.OrderBy(item => item.Sequence).Select(item => new FollowUpEvidence(item.Sequence,
            item.Source, item.Observation, item.CheckedScope, item.RecordedAtUtc)).ToArray(),
        entity.Changes.OrderBy(item => item.Sequence).Select(item => new FollowUpChange(item.Sequence,
            item.Kind, item.OccurredAtUtc, item.PreviousDueAtUtc, item.NewDueAtUtc)).ToArray());

    internal static FollowUpEntity ToEntity(FollowUpSnapshot item)
    {
        var entity = new FollowUpEntity
        {
            Id = item.Id, Title = item.Title, Condition = item.Condition, DueAtUtc = item.DueAtUtc,
            TimeZoneId = item.TimeZoneId, ItemId = item.ItemId, Source = item.Source,
            Status = item.Status, Draft = item.Draft, Version = 1
        };
        foreach (var evidence in item.Evidence) entity.Evidence.Add(ToEntity(item.Id, evidence));
        foreach (var change in item.Changes) entity.Changes.Add(ToEntity(item.Id, change));
        return entity;
    }

    private static FollowUpEvidenceEntity ToEntity(Guid id, FollowUpEvidence item) => new()
    {
        FollowUpId = id, Sequence = item.Sequence, Source = item.Source, Observation = item.Observation,
        CheckedScope = item.CheckedScope, RecordedAtUtc = item.RecordedAtUtc
    };

    private static FollowUpChangeEntity ToEntity(Guid id, FollowUpChange item) => new()
    {
        FollowUpId = id, Sequence = item.Sequence, Kind = item.Kind, OccurredAtUtc = item.OccurredAtUtc,
        PreviousDueAtUtc = item.PreviousDueAtUtc, NewDueAtUtc = item.NewDueAtUtc
    };

    private static DomainResult<T> Failure<T>(DomainErrorCode code, string message) =>
        DomainResult<T>.Failure(new DomainError(code, message));
}
