using System.Data;
using Bujo.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Infrastructure.Persistence;

public sealed class EfJournalRepository(IDbContextFactory<JournalDbContext> contextFactory) : IJournalRepository
{
    private static readonly Guid FutureLogId = Guid.Parse("00000000-0000-0000-0000-000000000101");

    public JournalSnapshot GetSnapshot()
    {
        using var context = contextFactory.CreateDbContext();
        var document = context.JournalDocuments.AsNoTracking().SingleOrDefault(item => item.Id == 1);
        return document is null
            ? InMemoryJournalRepository.CreateWithFutureLog(FutureLogId).GetSnapshot()
            : JournalStateSerializer.Deserialize(document.Payload);
    }

    public DomainResult<JournalListSnapshot> CreateList(CreateListCommand command) =>
        Execute((_, repository) => repository.CreateList(command));

    public DomainResult<CaptureResult> Capture(CaptureCommand command) =>
        Execute((_, repository) => repository.Capture(command));

    public DomainResult<MoveResult> Move(MoveCommand command) =>
        Execute((context, repository) =>
        {
            var fingerprint = Fingerprint(command.ItemId, command.ExpectedCurrentOccurrenceId, command.DestinationListId);
            var prior = context.MoveOperations.AsNoTracking()
                .SingleOrDefault(item => item.OperationId == command.OperationId);
            if (prior is not null)
            {
                if (!StringComparer.Ordinal.Equals(prior.Fingerprint, fingerprint))
                {
                    return DomainResult<MoveResult>.Failure(new DomainError(
                        DomainErrorCode.IdempotencyKeyReuse,
                        "The operation id was already used for a different move."));
                }

                return DomainResult<MoveResult>.Success(JournalStateSerializer.DeserializeMoveResult(prior.ResultJson));
            }

            var result = repository.Move(command);
            if (result.IsSuccess)
            {
                context.MoveOperations.Add(new MoveOperationEntity
                {
                    OperationId = command.OperationId,
                    ItemId = command.ItemId,
                    ExpectedOccurrenceId = command.ExpectedCurrentOccurrenceId,
                    DestinationListId = command.DestinationListId,
                    Fingerprint = fingerprint,
                    ResultJson = JournalStateSerializer.SerializeMoveResult(result.Value!)
                });
            }

            return result;
        });

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
                context.SaveChanges();
                transaction.Commit();
                return result;
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainResult<T>.Failure(new DomainError(
                DomainErrorCode.ConcurrencyConflict,
                "The journal changed while the operation was being saved."));
        }
        catch (DbUpdateException)
        {
            return DomainResult<T>.Failure(new DomainError(
                DomainErrorCode.ConcurrencyConflict,
                "The journal operation conflicts with a concurrent change."));
        }
    }

    private static InMemoryJournalRepository LoadRepository(JournalDbContext context)
    {
        var document = context.JournalDocuments.SingleOrDefault(item => item.Id == 1);
        return document is null
            ? InMemoryJournalRepository.CreateWithFutureLog(FutureLogId)
            : new InMemoryJournalRepository(JournalStateSerializer.Deserialize(document.Payload));
    }

    private static void SaveRepository(JournalDbContext context, JournalSnapshot snapshot)
    {
        var document = context.JournalDocuments.SingleOrDefault(item => item.Id == 1);
        if (document is null)
        {
            context.JournalDocuments.Add(new JournalDocumentEntity
            {
                Id = 1,
                Payload = JournalStateSerializer.Serialize(snapshot)
            });
        }
        else
        {
            document.Payload = JournalStateSerializer.Serialize(snapshot);
        }

        context.OpenOccurrences.ExecuteDelete();
        context.OpenOccurrences.AddRange(snapshot.Occurrences
            .Where(item => item.State == OccurrenceState.Open)
            .Select(item => new OpenOccurrenceEntity
            {
                OccurrenceId = item.Id,
                ItemId = item.ItemId,
                ListId = item.ListId,
                State = item.State
            }));
    }

    private static string Fingerprint(Guid itemId, Guid expectedOccurrenceId, Guid destinationListId) =>
        $"{itemId:N}:{expectedOccurrenceId:N}:{destinationListId:N}";
}
