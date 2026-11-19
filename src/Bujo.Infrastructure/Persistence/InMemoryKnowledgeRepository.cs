using Bujo.Domain;

namespace Bujo.Infrastructure.Persistence;

public sealed class InMemoryKnowledgeRepository : IKnowledgeRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, KnowledgeEntry> _entries = [];
    private readonly KnowledgeIndexState _indexState;

    public InMemoryKnowledgeRepository(KnowledgeIndexState indexState = KnowledgeIndexState.Unavailable) =>
        _indexState = indexState;

    public DomainResult<KnowledgeEntrySnapshot> Create(CreateKnowledgeEntryCommand command)
    {
        lock (_gate)
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
                if (!_entries.TryAdd(entry.Id, entry))
                {
                    return Failure<KnowledgeEntrySnapshot>(
                        DomainErrorCode.InvariantViolation,
                        "A knowledge entry with this ID already exists.");
                }

                return DomainResult<KnowledgeEntrySnapshot>.Success(entry.ToSnapshot());
            }
            catch (DomainException exception)
            {
                return DomainResult<KnowledgeEntrySnapshot>.Failure(exception.ToError());
            }
        }
    }

    public DomainResult<KnowledgeEntrySnapshot> Get(Guid id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry)
                ? DomainResult<KnowledgeEntrySnapshot>.Success(entry.ToSnapshot())
                : NotFound<KnowledgeEntrySnapshot>();
        }
    }

    public DomainResult<KnowledgeEntrySnapshot> Update(UpdateKnowledgeEntryCommand command)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(command.Id, out var entry))
            {
                return NotFound<KnowledgeEntrySnapshot>();
            }

            if (entry.Version != command.ExpectedVersion)
            {
                return ConcurrencyFailure<KnowledgeEntrySnapshot>();
            }

            try
            {
                return DomainResult<KnowledgeEntrySnapshot>.Success(entry.Edit(
                    command.Content,
                    command.Title,
                    command.SourceReference,
                    command.Tags,
                    command.UpdatedAt));
            }
            catch (DomainException exception)
            {
                return DomainResult<KnowledgeEntrySnapshot>.Failure(exception.ToError());
            }
        }
    }

    public DomainResult<KnowledgeEntrySnapshot> SetArchived(
        Guid id,
        bool archived,
        long expectedVersion,
        DateTimeOffset updatedAt)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return NotFound<KnowledgeEntrySnapshot>();
            }

            if (entry.Version != expectedVersion)
            {
                return ConcurrencyFailure<KnowledgeEntrySnapshot>();
            }

            try
            {
                return DomainResult<KnowledgeEntrySnapshot>.Success(entry.SetArchived(archived, updatedAt));
            }
            catch (DomainException exception)
            {
                return DomainResult<KnowledgeEntrySnapshot>.Failure(exception.ToError());
            }
        }
    }

    public DomainResult<KnowledgeEntrySnapshot> Delete(Guid id, long expectedVersion)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return NotFound<KnowledgeEntrySnapshot>();
            }

            if (entry.Version != expectedVersion)
            {
                return ConcurrencyFailure<KnowledgeEntrySnapshot>();
            }

            _entries.Remove(id);
            return DomainResult<KnowledgeEntrySnapshot>.Success(entry.ToSnapshot());
        }
    }

    public DomainResult<KnowledgeSearchPage> Search(KnowledgeSearchCriteria criteria)
    {
        lock (_gate)
        {
            var mode = _indexState == KnowledgeIndexState.Ready
                ? KnowledgeSearchMode.FullText
                : KnowledgeSearchMode.Fallback;
            var entries = _entries.Values
                .Where(entry => criteria.IncludeArchived || entry.ArchivedAt is null)
                .OrderByDescending(entry => entry.UpdatedAt)
                .ThenBy(entry => entry.Id)
                .Take(_indexState == KnowledgeIndexState.Ready
                    ? int.MaxValue
                    : DomainLimits.KnowledgeFallbackEntries)
                .Select(entry => entry.ToSnapshot())
                .Where(entry =>
                    criteria.Tags.All(tag => entry.Tags.Any(entryTag =>
                        entryTag.NormalizedValue == tag.NormalizedValue)) &&
                    criteria.Terms.All(term =>
                        entry.Content.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        entry.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))
                .Select(entry => new KnowledgeSearchResult(
                    entry.Id,
                    entry.Content,
                    entry.Title,
                    entry.SourceReference,
                    entry.Tags,
                    entry.CreatedAt,
                    entry.UpdatedAt,
                    entry.ArchivedAt,
                    entry.Version,
                    mode == KnowledgeSearchMode.FullText ? CountMatches(entry, criteria.Terms) : 0))
                .OrderByDescending(result => result.Rank)
                .ThenByDescending(result => result.UpdatedAt)
                .ThenBy(result => result.Id)
                .Where(result => IsAfterCursor(result, criteria.Cursor))
                .Take(criteria.PageSize + 1)
                .ToArray();
            var hasMore = entries.Length > criteria.PageSize;
            var results = entries.Take(criteria.PageSize).ToArray();
            var nextCursor = hasMore && results.Length > 0
                ? new KnowledgeSearchCursor(
                    results[^1].Rank,
                    results[^1].UpdatedAt,
                    results[^1].Id,
                    criteria.QueryFingerprint)
                : null;

            return DomainResult<KnowledgeSearchPage>.Success(new KnowledgeSearchPage(
                results,
                nextCursor,
                mode,
                _indexState));
        }
    }

    public IReadOnlyList<KnowledgeTagInfo> GetTags()
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(entry => entry.ArchivedAt is null)
                .SelectMany(entry => entry.Tags.Select(tag => (tag, entry.Id)))
                .GroupBy(item => item.tag.NormalizedValue, StringComparer.Ordinal)
                .Select(group => new KnowledgeTagInfo(group.First().tag, group.Select(item => item.Id).Distinct().Count()))
                .OrderBy(item => item.Tag.NormalizedValue, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public KnowledgeIndexState GetIndexState() => _indexState;

    private static int CountMatches(KnowledgeEntrySnapshot entry, IReadOnlyList<string> terms)
    {
        var text = $"{entry.Title}\n{entry.Content}";
        return terms.Sum(term => CountOccurrences(text, term));
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += term.Length;
        }

        return count;
    }

    private static bool IsAfterCursor(KnowledgeSearchResult result, KnowledgeSearchCursor? cursor)
    {
        if (cursor is null)
        {
            return true;
        }

        return result.Rank < cursor.Rank ||
            result.Rank == cursor.Rank && result.UpdatedAt < cursor.UpdatedAt ||
            result.Rank == cursor.Rank && result.UpdatedAt == cursor.UpdatedAt && result.Id.CompareTo(cursor.Id) > 0;
    }

    private static DomainResult<T> NotFound<T>() =>
        DomainResult<T>.Failure(new DomainError(DomainErrorCode.NotFound, "The knowledge entry was not found."));

    private static DomainResult<T> ConcurrencyFailure<T>() =>
        DomainResult<T>.Failure(new DomainError(
            DomainErrorCode.ConcurrencyConflict,
            "The knowledge entry changed while the operation was being saved."));

    private static DomainResult<T> Failure<T>(DomainErrorCode code, string message) =>
        DomainResult<T>.Failure(new DomainError(code, message));
}
