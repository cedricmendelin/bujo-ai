namespace Bujo.Domain;

public sealed record CreateKnowledgeEntryCommand(
    Guid Id,
    string Content,
    string? Title,
    string? SourceReference,
    IReadOnlyList<string>? Tags,
    DateTimeOffset CreatedAt);

public sealed record UpdateKnowledgeEntryCommand(
    Guid Id,
    string Content,
    string? Title,
    string? SourceReference,
    IReadOnlyList<string>? Tags,
    long ExpectedVersion,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeSearchRequest(
    string? Text,
    IReadOnlyList<string>? Tags = null,
    bool IncludeArchived = false,
    int PageSize = 20,
    string? Cursor = null);

public sealed record KnowledgeSearchCursor(
    int Rank,
    DateTimeOffset UpdatedAt,
    Guid Id,
    string QueryFingerprint);

public sealed record KnowledgeSearchCriteria(
    IReadOnlyList<string> Terms,
    string FullTextExpression,
    IReadOnlyList<Tag> Tags,
    bool IncludeArchived,
    int PageSize,
    KnowledgeSearchCursor? Cursor,
    string QueryFingerprint);

public enum KnowledgeSearchMode
{
    FullText,
    Fallback
}

public enum KnowledgeIndexState
{
    Ready,
    Populating,
    Unavailable
}

public sealed record KnowledgeSearchResult(
    Guid Id,
    string Passage,
    string? Title,
    string? SourceReference,
    IReadOnlyList<Tag> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Version,
    int Rank);

public sealed record KnowledgeSearchPage(
    IReadOnlyList<KnowledgeSearchResult> Results,
    KnowledgeSearchCursor? NextCursor,
    KnowledgeSearchMode SearchMode,
    KnowledgeIndexState IndexState);

public sealed record KnowledgeSearchResponse(
    IReadOnlyList<KnowledgeSearchResult> Results,
    string? NextCursor,
    KnowledgeSearchMode SearchMode,
    KnowledgeIndexState IndexState);

public sealed record KnowledgeTagInfo(Tag Tag, int EntryCount);
