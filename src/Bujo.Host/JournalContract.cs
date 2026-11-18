using System.Globalization;
using Bujo.Domain;

namespace Bujo.Host;

internal static class JournalContract
{
    public static string ErrorCode(DomainErrorCode code) => code.ToString();

    public static int StatusCode(DomainErrorCode code) =>
        code switch
        {
            DomainErrorCode.NotFound => Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound,
            DomainErrorCode.ApprovalDenied => Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden,
            DomainErrorCode.InvalidInput or DomainErrorCode.InvalidTimeZone =>
                Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
            _ => Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict
        };

    public static DomainResult<PlannedPeriod> ParsePeriod(JournalEndpoints.PeriodRequest? request)
    {
        if (request is null || string.Equals(request.Kind, "unplanned", StringComparison.OrdinalIgnoreCase))
        {
            return DomainResult<PlannedPeriod>.Success(PlannedPeriod.Unplanned);
        }

        try
        {
            var period = request.Kind?.ToLowerInvariant() switch
            {
                "day" when DateOnly.TryParseExact(
                    request.Day,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day) => PlannedPeriod.ForDay(day),
                "week" when request.WeekYear.HasValue && request.Week.HasValue =>
                    PlannedPeriod.ForWeek(new IsoWeekKey(request.WeekYear.Value, request.Week.Value)),
                "month" when request.Year.HasValue && request.Month.HasValue =>
                    PlannedPeriod.ForMonth(request.Year.Value, request.Month.Value),
                "quarter" when request.Year.HasValue && request.Quarter.HasValue =>
                    PlannedPeriod.ForQuarter(request.Year.Value, request.Quarter.Value),
                "year" when request.Year.HasValue => PlannedPeriod.ForYear(request.Year.Value),
                _ => throw new DomainException(DomainErrorCode.InvalidInput, "The planned period is invalid.")
            };
            return DomainResult<PlannedPeriod>.Success(period);
        }
        catch (DomainException exception)
        {
            return DomainResult<PlannedPeriod>.Failure(exception.ToError());
        }
    }

    public static DomainResult<IsoWeekKey?> ParseWeekKey(JournalEndpoints.WeekKeyRequest? request)
    {
        if (request is null)
        {
            return DomainResult<IsoWeekKey?>.Success(null);
        }

        try
        {
            return DomainResult<IsoWeekKey?>.Success(new IsoWeekKey(request.Year, request.Week));
        }
        catch (DomainException exception)
        {
            return DomainResult<IsoWeekKey?>.Failure(exception.ToError());
        }
    }
}
