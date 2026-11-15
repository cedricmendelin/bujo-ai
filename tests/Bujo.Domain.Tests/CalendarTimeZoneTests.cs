using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class CalendarTimeZoneTests
{
    [Theory]
    [InlineData(2020, 12, 31, 2020, 53)]
    [InlineData(2021, 1, 1, 2020, 53)]
    [InlineData(2021, 1, 4, 2021, 1)]
    [InlineData(2026, 1, 1, 2026, 1)]
    public void Dates_map_to_iso_week_year_and_number(int year, int month, int day, int expectedYear, int expectedWeek)
    {
        var key = IsoWeekKey.FromDate(new DateOnly(year, month, day));

        Assert.Equal(expectedYear, key.Year);
        Assert.Equal(expectedWeek, key.Week);
        Assert.Equal(DayOfWeek.Monday, key.Monday.DayOfWeek);
    }

    [Fact]
    public void Today_uses_the_users_iana_zone_across_a_utc_day_boundary()
    {
        var fixture = new JournalTestFixture(
            new DateTimeOffset(2026, 4, 6, 6, 30, 0, TimeSpan.Zero),
            "America/Los_Angeles");

        Assert.Equal(new DateOnly(2026, 4, 5), fixture.Journal.Today);
    }

    [Theory]
    [InlineData("2024-03-10T09:30:00+00:00", 2024, 3, 10)]
    [InlineData("2024-03-10T10:30:00+00:00", 2024, 3, 10)]
    [InlineData("2024-11-03T08:30:00+00:00", 2024, 11, 3)]
    [InlineData("2024-11-03T09:30:00+00:00", 2024, 11, 3)]
    public void Today_is_stable_across_daylight_saving_transitions(string utc, int year, int month, int day)
    {
        var fixture = new JournalTestFixture(DateTimeOffset.Parse(utc), "America/Los_Angeles");

        Assert.Equal(new DateOnly(year, month, day), fixture.Journal.Today);
    }

    [Fact]
    public void Occurrence_instants_are_stored_in_utc()
    {
        var fixture = new JournalTestFixture(new DateTimeOffset(2026, 3, 8, 12, 30, 0, TimeSpan.FromHours(2)));

        var capture = fixture.Capture("UTC timestamp");

        Assert.Equal(TimeSpan.Zero, capture.Occurrence.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, capture.Occurrence.UpdatedAt.Offset);
    }

    [Fact]
    public void Invalid_time_zone_is_a_typed_domain_error()
    {
        var repository = new InMemory.InMemoryJournalRepository();

        var error = Assert.Throws<DomainException>(() => new JournalService(repository, ianaTimeZoneId: "Not/AZone"));

        Assert.Equal(DomainErrorCode.InvalidTimeZone, error.Code);
    }
}
