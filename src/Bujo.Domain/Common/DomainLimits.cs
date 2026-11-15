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
