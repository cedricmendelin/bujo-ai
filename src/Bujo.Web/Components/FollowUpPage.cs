using System.Globalization;
using Bujo.Application;
using Bujo.Domain;
using Microsoft.AspNetCore.Components;

namespace Bujo.Web.Components;

public abstract class FollowUpPage : PlanningPage
{
    [Inject] protected FollowUpApplicationService FollowUps { get; set; } = null!;

    protected TimeZoneInfo UserZone()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Zone)) throw new TimeZoneNotFoundException();
            return TimeZoneInfo.FindSystemTimeZoneById(Zone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new DomainException(DomainErrorCode.InvalidTimeZone,
                "Your time zone is unavailable. Check your browser settings and refresh.");
        }
    }

    protected DateTimeOffset ToUtc(string local)
    {
        if (!DateTime.TryParseExact(local, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new DomainException(DomainErrorCode.InvalidTimeZone, "Enter a valid local date and time.");

        var zone = UserZone();
        value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(value))
            throw new DomainException(DomainErrorCode.InvalidTimeZone,
                "This local time does not exist due to a clock change. Choose another time.");
        if (zone.IsAmbiguousTime(value))
            throw new DomainException(DomainErrorCode.InvalidTimeZone,
                "This local time occurs twice due to a clock change. Choose another time.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, zone), TimeSpan.Zero);
    }

    protected string DisplayDue(DateTimeOffset due) =>
        $"{TimeZoneInfo.ConvertTime(due, UserZone()):yyyy-MM-dd HH:mm} ({Zone})";
}
