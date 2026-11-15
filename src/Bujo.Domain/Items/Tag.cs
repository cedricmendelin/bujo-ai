namespace Bujo.Domain;

public sealed record Tag
{
    private Tag(string value, string normalizedValue)
    {
        Value = value;
        NormalizedValue = normalizedValue;
    }

    public string Value { get; }

    public string NormalizedValue { get; }

    public static Tag Create(string value)
    {
        var normalized = DomainLimits.RequiredText(value, DomainLimits.TagName, "Tag");
        return new Tag(normalized, normalized.ToUpperInvariant());
    }
}
