namespace Bujo.Domain;

public sealed class KnowledgeEntry
{
    private List<Tag> _tags;

    private KnowledgeEntry(
        Guid id,
        string content,
        string? title,
        string? sourceReference,
        IEnumerable<Tag> tags,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? archivedAt,
        long version)
    {
        Id = id;
        Content = content;
        Title = title;
        SourceReference = sourceReference;
        _tags = tags.ToList();
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ArchivedAt = archivedAt;
        Version = version;
    }

    public Guid Id { get; }

    public string Content { get; private set; }

    public string? Title { get; private set; }

    public string? SourceReference { get; private set; }

    public IReadOnlyList<Tag> Tags => _tags.AsReadOnly();

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public long Version { get; private set; }

    internal static KnowledgeEntry Create(
        Guid id,
        string content,
        string? title,
        string? sourceReference,
        IEnumerable<string>? tags,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw DomainLimits.Invalid("An entry ID is required.");
        }

        var exactContent = DomainLimits.RequiredVerbatimText(content, DomainLimits.KnowledgeContent, "Content");
        return new KnowledgeEntry(
            id,
            exactContent,
            DomainLimits.OptionalText(title, DomainLimits.KnowledgeTitle, "Title"),
            DomainLimits.OptionalText(sourceReference, DomainLimits.KnowledgeSourceReference, "Source reference"),
            NormalizeTags(tags),
            createdAt,
            createdAt,
            null,
            1);
    }

    internal static KnowledgeEntry FromSnapshot(KnowledgeEntrySnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.Content,
            snapshot.Title,
            snapshot.SourceReference,
            snapshot.Tags,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            snapshot.ArchivedAt,
            snapshot.Version);

    internal static KnowledgeEntry FromBackup(KnowledgeBackupEntry backup)
    {
        if (backup.Id == Guid.Empty || backup.Version < 1 ||
            backup.CreatedAt > backup.UpdatedAt ||
            backup.ArchivedAt is { } archivedAt &&
                (archivedAt < backup.CreatedAt || archivedAt > backup.UpdatedAt))
        {
            throw DomainLimits.Invalid("The knowledge backup contains invalid entry metadata.");
        }

        var exactContent = DomainLimits.RequiredVerbatimText(
            backup.Content,
            DomainLimits.KnowledgeContent,
            "Content");
        var title = DomainLimits.OptionalText(backup.Title, DomainLimits.KnowledgeTitle, "Title");
        var sourceReference = DomainLimits.OptionalText(
            backup.SourceReference,
            DomainLimits.KnowledgeSourceReference,
            "Source reference");
        var tags = (backup.Tags ?? [])
            .Select(Tag.Create)
            .ToArray();
        if (tags.Length > DomainLimits.KnowledgeTagsPerEntry ||
            tags.Select(tag => tag.NormalizedValue).Distinct(StringComparer.Ordinal).Count() != tags.Length)
        {
            throw DomainLimits.Invalid("The knowledge backup contains invalid or duplicate tags.");
        }

        return new KnowledgeEntry(
            backup.Id,
            exactContent,
            title,
            sourceReference,
            tags,
            backup.CreatedAt,
            backup.UpdatedAt,
            backup.ArchivedAt,
            backup.Version);
    }

    internal KnowledgeEntrySnapshot Edit(
        string content,
        string? title,
        string? sourceReference,
        IEnumerable<string>? tags,
        DateTimeOffset updatedAt)
    {
        var exactContent = DomainLimits.RequiredVerbatimText(content, DomainLimits.KnowledgeContent, "Content");
        var normalizedTitle = DomainLimits.OptionalText(title, DomainLimits.KnowledgeTitle, "Title");
        var normalizedSourceReference = DomainLimits.OptionalText(
            sourceReference,
            DomainLimits.KnowledgeSourceReference,
            "Source reference");
        var normalizedTags = NormalizeTags(tags).ToList();
        var timestamp = NextTimestamp(updatedAt);
        Content = exactContent;
        Title = normalizedTitle;
        SourceReference = normalizedSourceReference;
        _tags = normalizedTags;
        UpdatedAt = timestamp;
        Version++;
        return ToSnapshot();
    }

    internal KnowledgeEntrySnapshot SetArchived(bool archived, DateTimeOffset updatedAt)
    {
        if (archived == (ArchivedAt is not null))
        {
            return ToSnapshot();
        }

        var timestamp = NextTimestamp(updatedAt);
        ArchivedAt = archived ? timestamp : null;
        UpdatedAt = timestamp;
        Version++;
        return ToSnapshot();
    }

    internal KnowledgeEntrySnapshot ToSnapshot() =>
        new(Id, Content, Title, SourceReference, Tags.ToArray(), CreatedAt, UpdatedAt, ArchivedAt, Version);

    private DateTimeOffset NextTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp > UpdatedAt)
        {
            return timestamp;
        }

        if (UpdatedAt == DateTimeOffset.MaxValue)
        {
            throw DomainLimits.Invalid("The entry timestamp cannot be advanced.");
        }

        return UpdatedAt.AddTicks(1);
    }

    private static IReadOnlyList<Tag> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var result = new List<Tag>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in tags)
        {
            var tag = Tag.Create(value);
            if (keys.Add(tag.NormalizedValue))
            {
                result.Add(tag);
            }
        }

        if (result.Count > DomainLimits.KnowledgeTagsPerEntry)
        {
            throw DomainLimits.Invalid(
                $"A knowledge entry cannot have more than {DomainLimits.KnowledgeTagsPerEntry} tags.");
        }

        return result;
    }
}

public sealed record KnowledgeEntrySnapshot(
    Guid Id,
    string Content,
    string? Title,
    string? SourceReference,
    IReadOnlyList<Tag> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Version);

public sealed record KnowledgeBackupSnapshot(
    int SchemaVersion,
    IReadOnlyList<KnowledgeBackupEntry> Entries);

public sealed record KnowledgeBackupEntry(
    Guid Id,
    string Content,
    string? Title,
    string? SourceReference,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long Version);
