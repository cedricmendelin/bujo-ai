using System.Data;
using Bujo.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Infrastructure.Persistence;

public sealed class EfJournalRepository(IDbContextFactory<JournalDbContext> contextFactory) : IJournalRepository
{
    private static readonly Guid FutureLogId = Guid.Parse("00000000-0000-0000-0000-000000000101");

    public JournalSnapshot GetSnapshot()
    {
        using var context = contextFactory.CreateDbContext();
        return LoadSnapshot(context, tracked: false);
    }

    public DomainResult<JournalSnapshot> RestoreSnapshot(JournalSnapshot snapshot)
    {
        try
        {
            InMemoryJournalRepository.ValidateSnapshot(snapshot);
            using var strategyContext = contextFactory.CreateDbContext();
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            return strategy.Execute(() =>
            {
                using var context = contextFactory.CreateDbContext();
                using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
                ReplaceJournal(context, snapshot);
                context.SaveChanges();
                transaction.Commit();
                return DomainResult<JournalSnapshot>.Success(LoadSnapshot(context, tracked: false));
            });
        }
        catch (Exception exception) when (
            exception is ArgumentException or DomainException or InvalidOperationException)
        {
            return DomainResult<JournalSnapshot>.Failure(new DomainError(
                DomainErrorCode.InvalidInput,
                "The journal backup is invalid."));
        }
        catch (DbUpdateException)
        {
            return DomainResult<JournalSnapshot>.Failure(new DomainError(
                DomainErrorCode.InvariantViolation,
                "The journal backup could not be restored."));
        }
    }

    public DomainResult<JournalListSnapshot> CreateList(CreateListCommand command) =>
        Execute((_, repository) => repository.CreateList(command));

    public DomainResult<CaptureResult> Capture(CaptureCommand command) =>
        Execute((_, repository) => repository.Capture(command));

    public DomainResult<MoveResult> Move(MoveCommand command)
    {
        var fingerprint = Fingerprint(command.ItemId, command.ExpectedCurrentOccurrenceId, command.DestinationListId);
        var result = Execute((context, repository) =>
        {
            var prior = context.MoveOperations.SingleOrDefault(item => item.OperationId == command.OperationId);
            if (prior is not null)
            {
                return ReadPriorMove(prior, fingerprint);
            }

            var moved = repository.Move(command);
            if (moved.IsSuccess)
            {
                context.MoveOperations.Add(new MoveOperationEntity
                {
                    OperationId = command.OperationId,
                    ItemId = command.ItemId,
                    ExpectedOccurrenceId = command.ExpectedCurrentOccurrenceId,
                    DestinationListId = command.DestinationListId,
                    Fingerprint = fingerprint,
                    ResultJson = JournalStateSerializer.SerializeMoveResult(moved.Value!)
                });
            }

            return moved;
        });

        if (result.IsSuccess || result.Error?.Code != DomainErrorCode.ConcurrencyConflict)
        {
            return result;
        }

        using var retryContext = contextFactory.CreateDbContext();
        var committedMove = retryContext.MoveOperations.AsNoTracking()
            .SingleOrDefault(item => item.OperationId == command.OperationId);
        return committedMove is null ? result : ReadPriorMove(committedMove, fingerprint);
    }

    public DomainResult<OccurrenceSnapshot> Transition(TransitionCommand command) =>
        Execute((_, repository) => repository.Transition(command));

    public DomainResult<JournalListSnapshot> CloseList(ListVersionCommand command) =>
        Execute((_, repository) => repository.CloseList(command));

    public DomainResult<JournalListSnapshot> ReopenList(ListVersionCommand command) =>
        Execute((_, repository) => repository.ReopenList(command));

    public DomainResult<ItemSnapshot> EditItem(EditItemCommand command) =>
        Execute((_, repository) => repository.EditItem(command));

    public DomainResult<ItemSnapshot> ChangePlannedPeriod(ChangePeriodCommand command) =>
        Execute((_, repository) => repository.ChangePlannedPeriod(command));

    public DomainResult<JournalListSnapshot> SetListGoal(SetListGoalCommand command) =>
        Execute((_, repository) => repository.SetListGoal(command));

    public DomainResult<ReviewProposalSnapshot> CreateReviewProposal(CreateReviewProposalCommand command) =>
        Execute((_, repository) => repository.CreateReviewProposal(command));

    public DomainResult<ReviewProposalSnapshot> GetReviewProposal(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        var entity = context.ReviewProposals.AsNoTracking().SingleOrDefault(proposal => proposal.Id == id);
        return entity is null
            ? DomainResult<ReviewProposalSnapshot>.Failure(
                new DomainError(DomainErrorCode.NotFound, "The review proposal was not found."))
            : DomainResult<ReviewProposalSnapshot>.Success(ToSnapshot(entity));
    }

    public DomainResult<ReviewProposalSnapshot> SetReviewProposalStatus(ReviewProposalStatusCommand command) =>
        Execute((_, repository) => repository.SetReviewProposalStatus(command));

    public DomainResult<ReviewProposalSnapshot> ApplyReviewProposal(ApplyReviewProposalCommand command) =>
        Execute((_, repository) => repository.ApplyReviewProposal(command));

    private DomainResult<T> Execute<T>(Func<JournalDbContext, InMemoryJournalRepository, DomainResult<T>> command)
    {
        try
        {
            using var strategyContext = contextFactory.CreateDbContext();
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            return strategy.Execute(() =>
            {
                using var context = contextFactory.CreateDbContext();
                using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
                var repository = LoadRepository(context);
                var result = command(context, repository);
                if (!result.IsSuccess)
                {
                    transaction.Rollback();
                    return result;
                }

                SaveRepository(context, repository.GetSnapshot());
                SaveReviewProposals(context, repository.GetReviewProposals());
                context.SaveChanges();
                transaction.Commit();
                return result;
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure<T>();
        }
        catch (DbUpdateException exception) when (IsUniqueIndexViolation(exception, "IX_JournalLists_NormalizedName"))
        {
            return DomainFailure<T>(
                DomainErrorCode.DuplicateListName,
                "A list with this name already exists.");
        }
        catch (DbUpdateException exception) when (IsConstraintViolation(exception))
        {
            return ConcurrencyFailure<T>();
        }
    }

    private static DomainResult<T> DomainFailure<T>(DomainErrorCode code, string message) =>
        DomainResult<T>.Failure(new DomainError(code, message));

    private static DomainResult<T> ConcurrencyFailure<T>() =>
        DomainResult<T>.Failure(new DomainError(
            DomainErrorCode.ConcurrencyConflict,
            "The journal changed while the operation was being saved."));

    private static bool IsUniqueIndexViolation(DbUpdateException exception, string indexName) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 } sqlException &&
        sqlException.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase);

    private static bool IsConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 or 547 };

    private static DomainResult<MoveResult> ReadPriorMove(MoveOperationEntity prior, string fingerprint) =>
        StringComparer.Ordinal.Equals(prior.Fingerprint, fingerprint)
            ? DomainResult<MoveResult>.Success(JournalStateSerializer.DeserializeMoveResult(prior.ResultJson))
            : DomainResult<MoveResult>.Failure(new DomainError(
                DomainErrorCode.IdempotencyKeyReuse,
                "The operation id was already used for a different move."));

    private static InMemoryJournalRepository LoadRepository(JournalDbContext context) =>
        new(LoadSnapshot(context, tracked: true), LoadReviewProposals(context, tracked: true));

    private static IReadOnlyList<ReviewProposalSnapshot> LoadReviewProposals(JournalDbContext context, bool tracked)
    {
        var proposals = tracked ? context.ReviewProposals : context.ReviewProposals.AsNoTracking();
        return proposals.OrderBy(proposal => proposal.CreatedAt).ToArray().Select(ToSnapshot).ToArray();
    }

    private static void SaveReviewProposals(
        JournalDbContext context,
        IReadOnlyList<ReviewProposalSnapshot> proposals)
    {
        var entities = context.ReviewProposals.Local.ToDictionary(proposal => proposal.Id);
        foreach (var proposal in proposals)
        {
            if (!entities.TryGetValue(proposal.Id, out var entity))
            {
                entity = new ReviewProposalEntity
                {
                    Id = proposal.Id,
                    CreatedBy = proposal.CreatedBy,
                    ChangesJson = JournalStateSerializer.SerializeReviewChanges(proposal.Changes),
                    Digest = proposal.Digest
                };
                context.ReviewProposals.Add(entity);
                entities.Add(entity.Id, entity);
            }

            entity.ListId = proposal.ListId;
            entity.ExpectedListVersion = proposal.ExpectedListVersion;
            entity.CreatedBy = proposal.CreatedBy;
            entity.Source = proposal.Source;
            entity.CreatedAt = proposal.CreatedAt;
            entity.ExpiresAt = proposal.ExpiresAt;
            entity.Status = proposal.Status;
            entity.RequestCloseList = proposal.RequestCloseList;
            entity.ChangesJson = JournalStateSerializer.SerializeReviewChanges(proposal.Changes);
            entity.Digest = proposal.Digest;
            entity.ApprovedBy = proposal.ApprovedBy;
            entity.ApprovedAt = proposal.ApprovedAt;
            entity.ApprovedDigest = proposal.ApprovedDigest;
            entity.AppliedAt = proposal.AppliedAt;
        }
    }

    private static ReviewProposalSnapshot ToSnapshot(ReviewProposalEntity proposal) =>
        new(
            proposal.Id,
            proposal.ListId,
            proposal.ExpectedListVersion,
            proposal.CreatedBy,
            proposal.Source,
            proposal.CreatedAt,
            proposal.ExpiresAt,
            proposal.Status,
            proposal.RequestCloseList,
            JournalStateSerializer.DeserializeReviewChanges(proposal.ChangesJson),
            proposal.Digest,
            proposal.ApprovedBy,
            proposal.ApprovedAt,
            proposal.ApprovedDigest,
            proposal.AppliedAt);

    internal static JournalSnapshot LoadSnapshot(JournalDbContext context, bool tracked)
    {
        var lists = (tracked ? context.Lists : context.Lists.AsNoTracking())
            .OrderBy(item => item.Id)
            .ToArray();
        if (lists.Length == 0)
        {
            return InMemoryJournalRepository.CreateWithFutureLog(FutureLogId).GetSnapshot();
        }

        var itemsQuery = tracked ? context.Items : context.Items.AsNoTracking();
        var items = itemsQuery
            .Include(item => item.Tags)
            .OrderBy(item => item.Id)
            .ToArray()
            .Select(item => new ItemSnapshot(
                item.Id,
                item.Name,
                item.Description,
                item.Tags.OrderBy(tag => tag.NormalizedValue).Select(tag => Tag.Create(tag.Value)).ToArray(),
                ToPlannedPeriod(item),
                item.CurrentOccurrenceId,
                item.Version,
                item.Type))
            .ToArray();

        var listSnapshots = lists.Select(item => new JournalListSnapshot(
            item.Id,
            item.Name,
            item.Kind,
            item.State,
            item.GoalText,
            item.WeekYear is { } year && item.WeekNumber is { } week ? new IsoWeekKey(year, week) : null,
            item.Version,
            item.PeriodYear is { } periodYear
                ? new JournalPeriodKey(item.Kind, periodYear, item.PeriodMonth, item.PeriodQuarter)
                : null)).ToArray();
        var occurrencesQuery = tracked ? context.Occurrences : context.Occurrences.AsNoTracking();
        var occurrenceEntities = occurrencesQuery
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Id)
            .ToArray();
        var occurrences = occurrenceEntities.Select(item => new OccurrenceSnapshot(
                item.Id,
                item.ItemId,
                item.ListId,
                item.Sequence,
                item.State,
                item.CreatedAt,
                item.UpdatedAt,
                item.PreviousOccurrenceId,
                item.NextOccurrenceId,
                item.Version))
            .ToArray();
        var eventsQuery = tracked ? context.JournalEvents : context.JournalEvents.AsNoTracking();
        var eventEntities = eventsQuery
            .OrderBy(item => item.Sequence)
            .ToArray();
        var history = eventEntities.Select(item => new JournalEvent(
                item.Sequence,
                item.Kind,
                item.OccurredAt,
                item.ItemId,
                item.OccurrenceId,
                item.PreviousOccurrenceId,
                item.NextOccurrenceId,
                item.ListId,
                item.OperationId))
            .ToArray();

        return JournalSnapshot.Restore(items, listSnapshots, occurrences, history);
    }

    internal static void ReplaceJournal(JournalDbContext context, JournalSnapshot snapshot)
    {
        context.MoveOperations.ExecuteDelete();
        context.ReviewProposals.ExecuteDelete();
        context.JournalEvents.ExecuteDelete();
        context.Occurrences.ExecuteDelete();
        context.ItemTags.ExecuteDelete();
        context.Items.ExecuteDelete();
        context.Lists.ExecuteDelete();
        SaveRepository(context, snapshot);
    }

    internal static void SaveRepository(JournalDbContext context, JournalSnapshot snapshot)
    {
        var lists = context.Lists.Local.ToDictionary(item => item.Id);
        foreach (var list in snapshot.Lists)
        {
            if (!lists.TryGetValue(list.Id, out var entity))
            {
                entity = new JournalListEntity
                {
                    Id = list.Id,
                    Name = list.Name,
                    NormalizedName = list.Name
                };
                context.Lists.Add(entity);
                lists.Add(entity.Id, entity);
            }

            entity.Name = list.Name;
            entity.NormalizedName = list.Name;
            entity.Kind = list.Kind;
            entity.State = list.State;
            entity.GoalText = list.GoalText;
            entity.WeekYear = list.WeekKey?.Year;
            entity.WeekNumber = list.WeekKey?.Week;
            entity.PeriodYear = list.PeriodKey?.Year;
            entity.PeriodMonth = list.PeriodKey?.Month;
            entity.PeriodQuarter = list.PeriodKey?.Quarter;
            entity.Version = list.Version;
        }

        var items = context.Items.Local.ToDictionary(item => item.Id);
        foreach (var item in snapshot.Items)
        {
            if (!items.TryGetValue(item.Id, out var entity))
            {
                entity = new ItemEntity { Id = item.Id, Name = item.Name };
                context.Items.Add(entity);
                items.Add(entity.Id, entity);
            }

            entity.Name = item.Name;
            entity.Description = item.Description;
            entity.Type = item.Type;
            entity.CurrentOccurrenceId = item.CurrentOccurrenceId;
            entity.Version = item.Version;
            SetPlannedPeriod(entity, item.PlannedPeriod);
            SaveTags(context, entity, item.Tags);
        }

        var occurrences = context.Occurrences.Local.ToDictionary(item => item.Id);
        foreach (var occurrence in snapshot.Occurrences)
        {
            if (!occurrences.TryGetValue(occurrence.Id, out var entity))
            {
                entity = new OccurrenceEntity { Id = occurrence.Id };
                context.Occurrences.Add(entity);
                occurrences.Add(entity.Id, entity);
            }

            entity.ItemId = occurrence.ItemId;
            entity.ListId = occurrence.ListId;
            entity.Sequence = occurrence.Sequence;
            entity.State = occurrence.State;
            entity.CreatedAt = occurrence.CreatedAt;
            entity.UpdatedAt = occurrence.UpdatedAt;
            entity.PreviousOccurrenceId = occurrence.PreviousOccurrenceId;
            entity.NextOccurrenceId = occurrence.NextOccurrenceId;
            entity.Version = occurrence.Version;
        }

        var history = context.JournalEvents.Local.Select(item => item.Sequence).ToHashSet();
        foreach (var entry in snapshot.History.Where(item => !history.Contains(item.Sequence)))
        {
            context.JournalEvents.Add(new JournalEventEntity
            {
                Sequence = entry.Sequence,
                Kind = entry.Kind,
                OccurredAt = entry.OccurredAt,
                ItemId = entry.ItemId,
                OccurrenceId = entry.OccurrenceId,
                PreviousOccurrenceId = entry.PreviousOccurrenceId,
                NextOccurrenceId = entry.NextOccurrenceId,
                ListId = entry.ListId,
                OperationId = entry.OperationId
            });
        }
    }

    private static void SaveTags(JournalDbContext context, ItemEntity item, IReadOnlyList<Tag> desiredTags)
    {
        var desired = desiredTags.ToDictionary(tag => tag.NormalizedValue);
        var existing = item.Tags.ToDictionary(tag => tag.NormalizedValue);
        foreach (var tag in existing.Values.Where(tag => !desired.ContainsKey(tag.NormalizedValue)))
        {
            context.ItemTags.Remove(tag);
        }

        foreach (var tag in desired.Values)
        {
            if (existing.TryGetValue(tag.NormalizedValue, out var entity))
            {
                entity.Value = tag.Value;
            }
            else
            {
                item.Tags.Add(new ItemTagEntity
                {
                    ItemId = item.Id,
                    NormalizedValue = tag.NormalizedValue,
                    Value = tag.Value
                });
            }
        }
    }

    private static PlannedPeriod ToPlannedPeriod(ItemEntity item) =>
        item.PlannedPeriodKind switch
        {
            PlannedPeriodKind.Unplanned => PlannedPeriod.Unplanned,
            PlannedPeriodKind.Day when item.PlannedDay is { } day => PlannedPeriod.ForDay(day),
            PlannedPeriodKind.Week when item.PlannedWeekYear is { } year && item.PlannedWeekNumber is { } week =>
                PlannedPeriod.ForWeek(new IsoWeekKey(year, week)),
            PlannedPeriodKind.Month when item.PlannedYear is { } year && item.PlannedMonth is { } month =>
                PlannedPeriod.ForMonth(year, month),
            PlannedPeriodKind.Quarter when item.PlannedYear is { } year && item.PlannedQuarter is { } quarter =>
                PlannedPeriod.ForQuarter(year, quarter),
            PlannedPeriodKind.Year when item.PlannedYear is { } year => PlannedPeriod.ForYear(year),
            _ => throw new InvalidOperationException("The stored planned period is invalid.")
        };

    private static void SetPlannedPeriod(ItemEntity item, PlannedPeriod period)
    {
        item.PlannedPeriodKind = period.Kind;
        item.PlannedDay = period.Day;
        item.PlannedWeekYear = period.Week?.Year;
        item.PlannedWeekNumber = period.Week?.Week;
        item.PlannedYear = period.Year;
        item.PlannedMonth = period.Month;
        item.PlannedQuarter = period.Quarter;
    }

    private static string Fingerprint(Guid itemId, Guid expectedOccurrenceId, Guid destinationListId) =>
        $"{itemId:N}:{expectedOccurrenceId:N}:{destinationListId:N}";
}
