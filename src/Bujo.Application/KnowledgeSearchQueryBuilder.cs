using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bujo.Domain;

namespace Bujo.Application;

internal static partial class KnowledgeSearchQueryBuilder
{
    private static readonly JsonSerializerOptions CursorOptions = new(JsonSerializerDefaults.Web);

    public static DomainResult<KnowledgeSearchCriteria> Build(KnowledgeSearchRequest request)
    {
        if (request.PageSize is < 1 or > DomainLimits.KnowledgePageSize)
        {
            return InvalidCriteria("Page size must be between 1 and 50.");
        }

        if (request.Text is { Length: > DomainLimits.KnowledgeSearchText })
        {
            return InvalidCriteria($"Search text must not exceed {DomainLimits.KnowledgeSearchText} characters.");
        }

        var terms = ParseTerms(request.Text ?? string.Empty);
        if (terms.Count > DomainLimits.KnowledgeSearchTerms)
        {
            return InvalidCriteria($"Search text cannot contain more than {DomainLimits.KnowledgeSearchTerms} terms.");
        }

        IReadOnlyList<Tag> tags;
        try
        {
            tags = (request.Tags ?? [])
                .Select(Tag.Create)
                .DistinctBy(tag => tag.NormalizedValue, StringComparer.Ordinal)
                .ToArray();
        }
        catch (DomainException exception)
        {
            return DomainResult<KnowledgeSearchCriteria>.Failure(exception.ToError());
        }

        if (tags.Count > DomainLimits.KnowledgeTagsPerEntry)
        {
            return InvalidCriteria($"Search cannot contain more than {DomainLimits.KnowledgeTagsPerEntry} tags.");
        }

        if (terms.Count == 0 && tags.Count == 0)
        {
            return InvalidCriteria("Provide search text or at least one tag.");
        }

        var fingerprint = Fingerprint(terms, tags, request.IncludeArchived);
        var cursor = DecodeCursor(request.Cursor);
        if (!cursor.IsSuccess)
        {
            return DomainResult<KnowledgeSearchCriteria>.Failure(cursor.Error!);
        }

        if (cursor.Value is { } value && value.QueryFingerprint != fingerprint)
        {
            return InvalidCriteria("The search cursor does not match this query.");
        }

        return DomainResult<KnowledgeSearchCriteria>.Success(new KnowledgeSearchCriteria(
            terms,
            string.Join(" AND ", terms.Select(term => $"\"{term.Replace("\"", "\"\"")}\"")),
            tags,
            request.IncludeArchived,
            request.PageSize,
            cursor.Value,
            fingerprint));
    }

    public static string? EncodeCursor(KnowledgeSearchCursor? cursor) =>
        cursor is null
            ? null
            : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, CursorOptions))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

    private static DomainResult<KnowledgeSearchCursor?> DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return DomainResult<KnowledgeSearchCursor?>.Success(null);
        }

        if (cursor.Length > 512)
        {
            return InvalidCursor("The search cursor is invalid.");
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var value = JsonSerializer.Deserialize<KnowledgeSearchCursor>(
                Convert.FromBase64String(base64),
                CursorOptions);
            return value is null || value.Id == Guid.Empty || value.Rank < 0 ||
                string.IsNullOrWhiteSpace(value.QueryFingerprint)
                ? InvalidCursor("The search cursor is invalid.")
                : DomainResult<KnowledgeSearchCursor?>.Success(value);
        }
        catch (FormatException)
        {
            return InvalidCursor("The search cursor is invalid.");
        }
        catch (JsonException)
        {
            return InvalidCursor("The search cursor is invalid.");
        }
    }

    private static List<string> ParseTerms(string text)
    {
        var terms = new List<string>();
        foreach (Match match in QueryTokenRegex().Matches(text))
        {
            var source = match.Groups["phrase"].Success
                ? match.Groups["phrase"].Value
                : match.Groups["term"].Value;
            var words = WordRegex().Matches(source).Select(item => item.Value).ToArray();
            var term = match.Groups["phrase"].Success ? string.Join(' ', words) : words.FirstOrDefault();
            if (!string.IsNullOrEmpty(term) && !terms.Contains(term, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(term);
            }
        }

        return terms;
    }

    private static string Fingerprint(
        IReadOnlyList<string> terms,
        IReadOnlyList<Tag> tags,
        bool includeArchived)
    {
        var value = $"{string.Join('\u001f', terms.Select(term => term.ToUpperInvariant()))}\u001e" +
            $"{string.Join('\u001f', tags.Select(tag => tag.NormalizedValue).Order(StringComparer.Ordinal))}\u001e" +
            (includeArchived ? "archived" : "active");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static DomainResult<KnowledgeSearchCriteria> InvalidCriteria(string message) =>
        DomainResult<KnowledgeSearchCriteria>.Failure(new DomainError(DomainErrorCode.InvalidInput, message));

    private static DomainResult<KnowledgeSearchCursor?> InvalidCursor(string message) =>
        DomainResult<KnowledgeSearchCursor?>.Failure(new DomainError(DomainErrorCode.InvalidInput, message));

    [GeneratedRegex("\"(?<phrase>[^\"]+)\"|(?<term>[\\p{L}\\p{Nd}]+)")]
    private static partial Regex QueryTokenRegex();

    [GeneratedRegex("[\\p{L}\\p{Nd}]+")]
    private static partial Regex WordRegex();
}
