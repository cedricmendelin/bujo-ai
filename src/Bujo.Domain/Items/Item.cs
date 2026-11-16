namespace Bujo.Domain;

public sealed class Item
{
    private List<Tag> _tags;

    private Item(
        Guid id,
        string name,
        string? description,
        IEnumerable<Tag> tags,
        PlannedPeriod plannedPeriod)
    {
        Id = id;
        Name = name;
        Description = description;
        _tags = tags.ToList();
        PlannedPeriod = plannedPeriod;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public IReadOnlyList<Tag> Tags => _tags.AsReadOnly();

    public PlannedPeriod PlannedPeriod { get; private set; }

    public Guid CurrentOccurrenceId { get; private set; }

    public long Version { get; private set; }

    internal static Item Create(
        Guid id,
        string name,
        string? description,
        IEnumerable<string>? tags,
        PlannedPeriod plannedPeriod)
    {
        return new Item(
            id,
            DomainLimits.RequiredText(name, DomainLimits.ItemName, "Item name"),
            DomainLimits.OptionalText(description, DomainLimits.ItemDescription, "Description"),
            NormalizeTags(tags),
            plannedPeriod);
    }

    internal static Item FromSnapshot(ItemSnapshot snapshot)
    {
        var item = new Item(snapshot.Id, snapshot.Name, snapshot.Description, snapshot.Tags, snapshot.PlannedPeriod)
        {
            CurrentOccurrenceId = snapshot.CurrentOccurrenceId,
            Version = snapshot.Version
        };
        return item;
    }

    public void Edit(string name, string? description, IEnumerable<string>? tags)
    {
        var normalizedName = DomainLimits.RequiredText(name, DomainLimits.ItemName, "Item name");
        var normalizedDescription = DomainLimits.OptionalText(description, DomainLimits.ItemDescription, "Description");
        var normalizedTags = NormalizeTags(tags).ToList();
        Name = normalizedName;
        Description = normalizedDescription;
        _tags = normalizedTags;
        Version++;
    }

    public void ChangePlannedPeriod(PlannedPeriod plannedPeriod)
    {
        PlannedPeriod = plannedPeriod;
        Version++;
    }

    internal void SetCurrentOccurrence(Guid occurrenceId)
    {
        CurrentOccurrenceId = occurrenceId;
        Version++;
    }

    internal void RecordChange() => Version++;

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

        if (result.Count > DomainLimits.TagsPerItem)
        {
            throw DomainLimits.Invalid($"An item cannot have more than {DomainLimits.TagsPerItem} tags.");
        }

        return result;
    }
}
