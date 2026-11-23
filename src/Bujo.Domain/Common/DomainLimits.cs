using System.Text;

namespace Bujo.Domain;

public static class DomainLimits
{
    public const int ItemName = 200;
    public const int ItemDescription = 10_000;
    public const int ListName = 100;
    public const int GoalText = 2_000;
    public const int TagName = 64;
    public const int TagsPerItem = 25;
    public const int KnowledgeContent = 100_000;
    public const int KnowledgeTitle = 200;
    public const int KnowledgeSourceReference = 2_048;
    public const int KnowledgeTagsPerEntry = 25;
    public const int KnowledgeSearchText = 256;
    public const int KnowledgeSearchTerms = 16;
    public const int KnowledgePageSize = 50;
    public const int KnowledgeFallbackEntries = 500;
    public const int KnowledgeBackupEntries = 10_000;
    public const int KnowledgeBackupBytes = 25_000_000;
    public const int JournalBackupBytes = 50_000_000;
    public const int FollowUpTitle = 200;
    public const int FollowUpCondition = 2_000;
    public const int FollowUpCheckedScope = 2_000;
    public const int FollowUpDraft = 10_000;
    public const int FollowUpTimeZone = 128;
    public const int FollowUpBackupEntries = 10_000;

    internal static string RequiredVerbatimText(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(DomainErrorCode.InvalidInput, $"{field} is required.");
        }

        if (value.Length > maxLength)
        {
            throw new DomainException(
                DomainErrorCode.InvalidInput,
                $"{field} must not exceed {maxLength} characters.");
        }

        return value;
    }

    internal static string RequiredText(string? value, int maxLength, string field)
    {
        var normalized = OptionalText(value, maxLength, field);
        if (normalized is null)
        {
            throw new DomainException(DomainErrorCode.InvalidInput, $"{field} is required.");
        }

        return normalized;
    }

    internal static string? OptionalText(string? value, int maxLength, string field)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length > maxLength)
        {
            throw new DomainException(
                DomainErrorCode.InvalidInput,
                $"{field} must not exceed {maxLength} characters.");
        }

        return normalized.Length == 0 ? null : normalized;
    }

    internal static DomainException Invalid(string message) =>
        new(DomainErrorCode.InvalidInput, message);
}
