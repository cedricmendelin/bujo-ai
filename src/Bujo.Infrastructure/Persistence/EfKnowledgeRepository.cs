using System.Data;
using System.Data.Common;
using Bujo.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bujo.Infrastructure.Persistence;

public sealed class EfKnowledgeRepository(
    IDbContextFactory<JournalDbContext> contextFactory,
    ILogger<EfKnowledgeRepository> logger) : IKnowledgeRepository
{
    private const string FullTextCatalog = "KnowledgeCatalog";

    public DomainResult<KnowledgeEntrySnapshot> Create(CreateKnowledgeEntryCommand command)
    {
        try
        {
            var entry = KnowledgeEntry.Create(
                command.Id,
                command.Content,
                command.Title,
                command.SourceReference,
                command.Tags,
                command.CreatedAt);
            using var context = contextFactory.CreateDbContext();
            var entity = ToEntity(entry.ToSnapshot());
            context.KnowledgeEntries.Add(entity);
            context.SaveChanges();
            return DomainResult<KnowledgeEntrySnapshot>.Success(ToSnapshot(entity));
        }
        catch (DomainException exception)
        {
            return DomainResult<KnowledgeEntrySnapshot>.Failure(exception.ToError());
        }
        catch (DbUpdateException)
        {
            return Failure<KnowledgeEntrySnapshot>(
                DomainErrorCode.InvariantViolation,
                "The knowledge entry could not be saved.");
        }
    }

    public DomainResult<KnowledgeEntrySnapshot> Get(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        var entity = context.KnowledgeEntries.AsNoTracking()
            .Include(entry => entry.Tags)
            .SingleOrDefault(entry => entry.Id == id);
        return entity is null
            ? NotFound<KnowledgeEntrySnapshot>()
            : DomainResult<KnowledgeEntrySnapshot>.Success(ToSnapshot(entity));
    }

    public DomainResult<KnowledgeEntrySnapshot> Update(UpdateKnowledgeEntryCommand command)
    {
        try
        {
            using var context = contextFactory.CreateDbContext();
            var entity = context.KnowledgeEntries
                .Include(entry => entry.Tags)
                .SingleOrDefault(entry => entry.Id == command.Id);
            if (entity is null)
            {
                return NotFound<KnowledgeEntrySnapshot>();
            }

            if (entity.Version != command.ExpectedVersion)
            {
                return ConcurrencyFailure<KnowledgeEntrySnapshot>();
            }

            var entry = KnowledgeEntry.FromSnapshot(ToSnapshot(entity));
            var updated = entry.Edit(
                command.Content,
                command.Title,
                command.SourceReference,
                command.Tags,
                command.UpdatedAt);
            ApplySnapshot(entity, updated);
            context.SaveChanges();
            return DomainResult<KnowledgeEntrySnapshot>.Success(ToSnapshot(entity));
        }
        catch (DomainException exception)
        {
            return DomainResult<KnowledgeEntrySnapshot>.Failure(exception.ToError());
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure<KnowledgeEntrySnapshot>();
        }
    }

    public DomainResult<KnowledgeEntrySnapshot> SetArchived(
        Guid id,
        bool archived,
        long expectedVersion,
        DateTimeOffset updatedAt)
    {
        try
        {
            using var context = contextFactory.CreateDbContext();
            var entity = context.KnowledgeEntries
                .Include(entry => entry.Tags)
                .SingleOrDefault(entry => entry.Id == id);
            if (entity is null)
            {
                return NotFound<KnowledgeEntrySnapshot>();
            }

            if (entity.Version != expectedVersion)
            {
                return ConcurrencyFailure<KnowledgeEntrySnapshot>();
            }

            var entry = KnowledgeEntry.FromSnapshot(ToSnapshot(entity));
            var updated = entry.SetArchived(archived, updatedAt);
            ApplySnapshot(entity, updated);
            context.SaveChanges();
            return DomainResult<KnowledgeEntrySnapshot>.Success(ToSnapshot(entity));
        }
        catch (DomainException exception)
        {
            return DomainResult<KnowledgeEntrySnapshot>.Failure(exception.ToError());
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure<KnowledgeEntrySnapshot>();
        }
    }

    public DomainResult<KnowledgeEntrySnapshot> Delete(Guid id, long expectedVersion)
    {
        using var context = contextFactory.CreateDbContext();
        var entity = context.KnowledgeEntries
            .Include(entry => entry.Tags)
            .SingleOrDefault(entry => entry.Id == id);
        if (entity is null)
        {
            return NotFound<KnowledgeEntrySnapshot>();
        }

        if (entity.Version != expectedVersion)
        {
            return ConcurrencyFailure<KnowledgeEntrySnapshot>();
        }

        var snapshot = ToSnapshot(entity);
        context.KnowledgeEntries.Remove(entity);
        try
        {
            context.SaveChanges();
            return DomainResult<KnowledgeEntrySnapshot>.Success(snapshot);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyFailure<KnowledgeEntrySnapshot>();
        }
    }

    public DomainResult<KnowledgeSearchPage> Search(KnowledgeSearchCriteria criteria)
    {
        var indexState = GetIndexState();
        if (indexState != KnowledgeIndexState.Ready || criteria.Terms.Count == 0)
        {
            return SearchFallback(criteria, indexState);
        }

        try
        {
            return SearchFullText(criteria);
        }
        catch (SqlException exception)
        {
            logger.LogWarning(
                "Knowledge full-text search failed ({FailureType}); using bounded fallback.",
                exception.GetType().Name);
            return SearchFallback(criteria, KnowledgeIndexState.Unavailable);
        }
    }

    public IReadOnlyList<KnowledgeTagInfo> GetTags()
    {
        using var context = contextFactory.CreateDbContext();
        return context.KnowledgeEntryTags.AsNoTracking()
            .Where(tag => context.KnowledgeEntries.Any(entry =>
                entry.Id == tag.KnowledgeEntryId && entry.ArchivedAt == null))
            .GroupBy(tag => tag.NormalizedValue)
            .Select(group => new
            {
                Value = group.OrderBy(tag => tag.Value).Select(tag => tag.Value).First(),
                NormalizedValue = group.Key,
                Count = group.Count()
            })
            .AsEnumerable()
            .OrderBy(item => item.NormalizedValue, StringComparer.Ordinal)
            .Select(item => new KnowledgeTagInfo(Tag.Create(item.Value), item.Count))
            .ToArray();
    }

    public KnowledgeIndexState GetIndexState()
    {
        try
        {
            using var context = contextFactory.CreateDbContext();
            var status = context.Database.SqlQueryRaw<int>($"""
                SELECT CASE
                    WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                         AND EXISTS (
                             SELECT 1 FROM sys.fulltext_indexes
                             WHERE object_id = OBJECT_ID(N'dbo.KnowledgeEntries'))
                    THEN ISNULL(FULLTEXTCATALOGPROPERTY(N'{FullTextCatalog}', N'PopulateStatus'), -1)
                    ELSE -1
                END AS [Value]
                """).Single();
            return status == 0
                ? KnowledgeIndexState.Ready
                : status < 0
                    ? KnowledgeIndexState.Unavailable
                    : KnowledgeIndexState.Populating;
        }
        catch (Exception exception) when (exception is SqlException or DbException or InvalidOperationException)
        {
            logger.LogWarning(
                "Knowledge full-text readiness probe failed ({FailureType}).",
                exception.GetType().Name);
            return KnowledgeIndexState.Unavailable;
        }
    }

    public KnowledgeBackupSnapshot ExportBackup()
    {
        using var context = contextFactory.CreateDbContext();
        return new KnowledgeBackupSnapshot(1, context.KnowledgeEntries.AsNoTracking()
            .Include(entry => entry.Tags)
            .OrderBy(entry => entry.Id)
            .ToArray()
            .Select(ToSnapshot)
            .Select(entry => new KnowledgeBackupEntry(
                entry.Id,
                entry.Content,
                entry.Title,
                entry.SourceReference,
                entry.Tags.Select(tag => tag.Value).ToArray(),
                entry.CreatedAt,
                entry.UpdatedAt,
                entry.ArchivedAt,
                entry.Version))
            .ToArray());
    }

    public DomainResult<KnowledgeBackupSnapshot> RestoreBackup(KnowledgeBackupSnapshot backup)
    {
        if (backup is null || backup.SchemaVersion != 1 || backup.Entries is null ||
            backup.Entries.Count > DomainLimits.KnowledgeBackupEntries)
        {
            return Failure<KnowledgeBackupSnapshot>(
                DomainErrorCode.InvalidInput,
                "The knowledge backup version or entry count is invalid.");
        }

        var restored = new Dictionary<Guid, KnowledgeEntry>();
        try
        {
            foreach (var item in backup.Entries)
            {
                if (item is null)
                {
                    return Failure<KnowledgeBackupSnapshot>(
                        DomainErrorCode.InvalidInput,
                        "The knowledge backup contains an empty entry.");
                }

                var entry = KnowledgeEntry.FromBackup(item);
                if (!restored.TryAdd(entry.Id, entry))
                {
                    return Failure<KnowledgeBackupSnapshot>(
                        DomainErrorCode.InvalidInput,
                        "The knowledge backup contains duplicate entry IDs.");
                }
            }
        }
        catch (DomainException exception)
        {
            return DomainResult<KnowledgeBackupSnapshot>.Failure(exception.ToError());
        }

        try
        {
            using var strategyContext = contextFactory.CreateDbContext();
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            strategy.Execute(() =>
            {
                using var context = contextFactory.CreateDbContext();
                using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
                context.KnowledgeEntries.ExecuteDelete();
                context.KnowledgeEntries.AddRange(restored.Values.Select(entry => ToEntity(entry.ToSnapshot())));
                context.SaveChanges();
                transaction.Commit();
            });
            return DomainResult<KnowledgeBackupSnapshot>.Success(ExportBackup());
        }
        catch (DbUpdateException)
        {
            return Failure<KnowledgeBackupSnapshot>(
                DomainErrorCode.InvariantViolation,
                "The knowledge backup could not be restored.");
        }
    }

    private DomainResult<KnowledgeSearchPage> SearchFullText(KnowledgeSearchCriteria criteria)
    {
        using var context = contextFactory.CreateDbContext();
        var sql = $"""
            SELECT TOP (@limit) entry.Id, CONVERT(int, search.[RANK]) AS Rank, entry.UpdatedAt
            FROM dbo.KnowledgeEntries AS entry
            INNER JOIN CONTAINSTABLE(
                dbo.KnowledgeEntries,
                (Content, Title),
                @query,
                LANGUAGE 1033) AS search ON search.[KEY] = entry.Id
            WHERE (@includeArchived = 1 OR entry.ArchivedAt IS NULL)
              {BuildTagFilter(criteria.Tags.Count)}
              AND (@hasCursor = 0 OR search.[RANK] < @cursorRank OR
                  (search.[RANK] = @cursorRank AND entry.UpdatedAt < @cursorUpdatedAt) OR
                  (search.[RANK] = @cursorRank AND entry.UpdatedAt = @cursorUpdatedAt AND entry.Id > @cursorId))
            ORDER BY search.[RANK] DESC, entry.UpdatedAt DESC, entry.Id ASC
            """;
        var parameters = CreateParameters(criteria);
        var matches = context.Database.SqlQueryRaw<KnowledgeSearchRow>(sql, parameters)
            .ToArray();
        var entries = LoadEntries(context, matches.Select(match => match.Id));
        var results = matches
            .Where(match => entries.ContainsKey(match.Id))
            .Select(match => ToResult(entries[match.Id], match.Rank))
            .ToArray();
        var hasMore = results.Length > criteria.PageSize;
        var page = results.Take(criteria.PageSize).ToArray();
        var next = hasMore
            ? new KnowledgeSearchCursor(
                page[^1].Rank,
                page[^1].UpdatedAt,
                page[^1].Id,
                criteria.QueryFingerprint)
            : null;
        return DomainResult<KnowledgeSearchPage>.Success(new KnowledgeSearchPage(
            page,
            next,
            KnowledgeSearchMode.FullText,
            KnowledgeIndexState.Ready));
    }

    private DomainResult<KnowledgeSearchPage> SearchFallback(
        KnowledgeSearchCriteria criteria,
        KnowledgeIndexState indexState)
    {
        using var context = contextFactory.CreateDbContext();
        var entries = context.KnowledgeEntries.AsNoTracking()
            .Include(entry => entry.Tags)
            .Where(entry => criteria.IncludeArchived || entry.ArchivedAt == null)
            .OrderByDescending(entry => entry.UpdatedAt)
            .ThenBy(entry => entry.Id)
            .Take(DomainLimits.KnowledgeFallbackEntries)
            .ToArray()
            .Select(ToSnapshot)
            .Where(entry =>
                criteria.Tags.All(tag => entry.Tags.Any(entryTag =>
                    entryTag.NormalizedValue == tag.NormalizedValue)) &&
                criteria.Terms.All(term =>
                    entry.Content.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    entry.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))
            .Select(entry => ToResult(entry, 0))
            .OrderByDescending(result => result.UpdatedAt)
            .ThenBy(result => result.Id)
            .Where(result => IsAfterCursor(result, criteria.Cursor))
            .Take(criteria.PageSize + 1)
            .ToArray();
        var hasMore = entries.Length > criteria.PageSize;
        var results = entries.Take(criteria.PageSize).ToArray();
        var next = hasMore
            ? new KnowledgeSearchCursor(
                0,
                results[^1].UpdatedAt,
                results[^1].Id,
                criteria.QueryFingerprint)
            : null;
        return DomainResult<KnowledgeSearchPage>.Success(new KnowledgeSearchPage(
            results,
            next,
            KnowledgeSearchMode.Fallback,
            indexState));
    }

    private static SqlParameter[] CreateParameters(KnowledgeSearchCriteria criteria)
    {
        var parameters = new List<SqlParameter>
        {
            new("@limit", SqlDbType.Int) { Value = criteria.PageSize + 1 },
            new("@query", SqlDbType.NVarChar, 4000) { Value = criteria.FullTextExpression },
            new("@includeArchived", SqlDbType.Bit) { Value = criteria.IncludeArchived },
            new("@hasCursor", SqlDbType.Bit) { Value = criteria.Cursor is not null },
            new("@cursorRank", SqlDbType.Int) { Value = criteria.Cursor?.Rank ?? 0 },
            new("@cursorUpdatedAt", SqlDbType.DateTimeOffset)
            {
                Value = criteria.Cursor?.UpdatedAt ?? DateTimeOffset.MinValue
            },
            new("@cursorId", SqlDbType.UniqueIdentifier) { Value = criteria.Cursor?.Id ?? Guid.Empty }
        };
        for (var index = 0; index < criteria.Tags.Count; index++)
        {
            parameters.Add(new SqlParameter($"@tag{index}", SqlDbType.NVarChar, DomainLimits.TagName)
            {
                Value = criteria.Tags[index].NormalizedValue
            });
        }

        return parameters.ToArray();
    }

    private static string BuildTagFilter(int tagCount)
    {
        if (tagCount == 0)
        {
            return string.Empty;
        }

        var parameters = string.Join(", ", Enumerable.Range(0, tagCount).Select(index => $"@tag{index}"));
        return $"""
              AND entry.Id IN (
                  SELECT tag.KnowledgeEntryId
                  FROM dbo.KnowledgeEntryTags AS tag
                  WHERE tag.NormalizedValue IN ({parameters})
                  GROUP BY tag.KnowledgeEntryId
                  HAVING COUNT(DISTINCT tag.NormalizedValue) = {tagCount}
              )
            """;
    }

    private static Dictionary<Guid, KnowledgeEntrySnapshot> LoadEntries(
        JournalDbContext context,
        IEnumerable<Guid> ids)
    {
        var values = ids.Distinct().ToArray();
        return values.Length == 0
            ? []
            : context.KnowledgeEntries.AsNoTracking()
                .Include(entry => entry.Tags)
                .Where(entry => values.Contains(entry.Id))
                .ToArray()
                .Select(entry => ToSnapshot(entry))
                .ToDictionary(entry => entry.Id);
    }

    private static KnowledgeEntryEntity ToEntity(KnowledgeEntrySnapshot snapshot)
    {
        var entity = new KnowledgeEntryEntity { Content = snapshot.Content };
        ApplySnapshot(entity, snapshot);
        return entity;
    }

    private static void ApplySnapshot(KnowledgeEntryEntity entity, KnowledgeEntrySnapshot snapshot)
    {
        entity.Id = snapshot.Id;
        entity.Content = snapshot.Content;
        entity.Title = snapshot.Title;
        entity.SourceReference = snapshot.SourceReference;
        entity.CreatedAt = snapshot.CreatedAt;
        entity.UpdatedAt = snapshot.UpdatedAt;
        entity.ArchivedAt = snapshot.ArchivedAt;
        entity.Version = snapshot.Version;
        var desired = snapshot.Tags.ToDictionary(tag => tag.NormalizedValue);
        var existing = entity.Tags.ToDictionary(tag => tag.NormalizedValue);
        foreach (var tag in existing.Values.Where(tag => !desired.ContainsKey(tag.NormalizedValue)))
        {
            entity.Tags.Remove(tag);
        }

        foreach (var tag in desired.Values)
        {
            if (existing.TryGetValue(tag.NormalizedValue, out var existingTag))
            {
                existingTag.Value = tag.Value;
            }
            else
            {
                entity.Tags.Add(new KnowledgeEntryTagEntity
                {
                    KnowledgeEntryId = entity.Id,
                    NormalizedValue = tag.NormalizedValue,
                    Value = tag.Value
                });
            }
        }
    }

    private static KnowledgeEntrySnapshot ToSnapshot(KnowledgeEntryEntity entity) =>
        new(
            entity.Id,
            entity.Content,
            entity.Title,
            entity.SourceReference,
            entity.Tags.OrderBy(tag => tag.NormalizedValue, StringComparer.Ordinal)
                .Select(tag => Tag.Create(tag.Value)).ToArray(),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.ArchivedAt,
            entity.Version);

    private static KnowledgeSearchResult ToResult(KnowledgeEntrySnapshot entry, int rank) =>
        new(
            entry.Id,
            entry.Content,
            entry.Title,
            entry.SourceReference,
            entry.Tags,
            entry.CreatedAt,
            entry.UpdatedAt,
            entry.ArchivedAt,
            entry.Version,
            rank);

    private static bool IsAfterCursor(KnowledgeSearchResult result, KnowledgeSearchCursor? cursor) =>
        cursor is null ||
        result.Rank < cursor.Rank ||
        result.Rank == cursor.Rank && result.UpdatedAt < cursor.UpdatedAt ||
        result.Rank == cursor.Rank && result.UpdatedAt == cursor.UpdatedAt && result.Id.CompareTo(cursor.Id) > 0;

    private static DomainResult<T> NotFound<T>() =>
        DomainResult<T>.Failure(new DomainError(DomainErrorCode.NotFound, "The knowledge entry was not found."));

    private static DomainResult<T> ConcurrencyFailure<T>() =>
        DomainResult<T>.Failure(new DomainError(
            DomainErrorCode.ConcurrencyConflict,
            "The knowledge entry changed while the operation was being saved."));

    private static DomainResult<T> Failure<T>(DomainErrorCode code, string message) =>
        DomainResult<T>.Failure(new DomainError(code, message));

    private sealed class KnowledgeSearchRow
    {
        public Guid Id { get; set; }

        public int Rank { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
