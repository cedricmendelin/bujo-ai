using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Bujo.Domain;

namespace Bujo.Infrastructure.Persistence;

internal static class JournalStateSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static string Serialize(JournalSnapshot snapshot)
    {
        var state = new PersistedJournalState(
            snapshot.Items.Select(item => new PersistedItem(
                item.Id,
                item.Name,
                item.Description,
                item.Tags.Select(tag => tag.Value).ToArray(),
                PersistedPeriod.From(item.PlannedPeriod),
                item.CurrentOccurrenceId,
                item.Version)).ToArray(),
            snapshot.Lists.Select(list => new PersistedList(
                list.Id,
                list.Name,
                list.Kind,
                list.State,
                list.GoalText,
                list.WeekKey?.Year,
                list.WeekKey?.Week,
                list.Version)).ToArray(),
            snapshot.Occurrences.ToArray(),
            snapshot.History.ToArray());
        return JsonSerializer.Serialize(state, Options);
    }

    public static JournalSnapshot Deserialize(string payload)
    {
        var state = JsonSerializer.Deserialize<PersistedJournalState>(payload, Options)
            ?? throw new JsonException("Journal state is empty.");
        var items = state.Items.Select(item => new ItemSnapshot(
            item.Id,
            item.Name,
            item.Description,
            item.Tags.Select(Tag.Create).ToArray(),
            item.PlannedPeriod.ToDomain(),
            item.CurrentOccurrenceId,
            item.Version)).ToArray();
        var lists = state.Lists.Select(list => new JournalListSnapshot(
            list.Id,
            list.Name,
            list.Kind,
            list.State,
            list.GoalText,
            list.WeekYear is { } year && list.WeekNumber is { } week ? new IsoWeekKey(year, week) : null,
            list.Version)).ToArray();
        return JournalSnapshot.Restore(items, lists, state.Occurrences, state.History);
    }

    public static string SerializeMoveResult(MoveResult result) =>
        JsonSerializer.Serialize(result, ResultOptions);

    public static MoveResult DeserializeMoveResult(string result) =>
        JsonSerializer.Deserialize<MoveResult>(result, ResultOptions)
        ?? throw new JsonException("Stored move result is empty.");

    private static JsonSerializerOptions ResultOptions { get; } = CreateResultOptions();

    private static JsonSerializerOptions CreateResultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TagConverter());
        options.Converters.Add(new PlannedPeriodConverter());
        return options;
    }

    private sealed class TagConverter : JsonConverter<Tag>
    {
        public override Tag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Tag.Create(reader.GetString() ?? throw new JsonException("Tag is empty."));

        public override void Write(Utf8JsonWriter writer, Tag value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class PlannedPeriodConverter : JsonConverter<PlannedPeriod>
    {
        public override PlannedPeriod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var kind = Enum.Parse<PlannedPeriodKind>(root.GetProperty("kind").GetString()!, ignoreCase: true);
            return kind switch
            {
                PlannedPeriodKind.Unplanned => PlannedPeriod.Unplanned,
                PlannedPeriodKind.Day => PlannedPeriod.ForDay(DateOnly.Parse(
                    root.GetProperty("day").GetString()!,
                    CultureInfo.InvariantCulture)),
                PlannedPeriodKind.Week => PlannedPeriod.ForWeek(new IsoWeekKey(
                    root.GetProperty("weekYear").GetInt32(),
                    root.GetProperty("weekNumber").GetInt32())),
                PlannedPeriodKind.Month => PlannedPeriod.ForMonth(
                    root.GetProperty("year").GetInt32(),
                    root.GetProperty("month").GetInt32()),
                PlannedPeriodKind.Quarter => PlannedPeriod.ForQuarter(
                    root.GetProperty("year").GetInt32(),
                    root.GetProperty("quarter").GetInt32()),
                PlannedPeriodKind.Year => PlannedPeriod.ForYear(root.GetProperty("year").GetInt32()),
                _ => throw new JsonException("Stored move result has an invalid planned period.")
            };
        }

        public override void Write(Utf8JsonWriter writer, PlannedPeriod value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", value.Kind.ToString());
            if (value.Day is { } day)
            {
                writer.WriteString("day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            if (value.Week is { } week)
            {
                writer.WriteNumber("weekYear", week.Year);
                writer.WriteNumber("weekNumber", week.Week);
            }

            if (value.Year is { } year)
            {
                writer.WriteNumber("year", year);
            }

            if (value.Month is { } month)
            {
                writer.WriteNumber("month", month);
            }

            if (value.Quarter is { } quarter)
            {
                writer.WriteNumber("quarter", quarter);
            }

            writer.WriteEndObject();
        }
    }

    private sealed record PersistedJournalState(
        PersistedItem[] Items,
        PersistedList[] Lists,
        OccurrenceSnapshot[] Occurrences,
        JournalEvent[] History);

    private sealed record PersistedItem(
        Guid Id,
        string Name,
        string? Description,
        string[] Tags,
        PersistedPeriod PlannedPeriod,
        Guid CurrentOccurrenceId,
        long Version);

    private sealed record PersistedList(
        Guid Id,
        string Name,
        JournalListKind Kind,
        JournalListState State,
        string? GoalText,
        int? WeekYear,
        int? WeekNumber,
        long Version);

    private sealed record PersistedPeriod(
        PlannedPeriodKind Kind,
        DateOnly? Day,
        int? WeekYear,
        int? WeekNumber,
        int? Year,
        int? Month,
        int? Quarter)
    {
        public static PersistedPeriod From(PlannedPeriod period) =>
            new(
                period.Kind,
                period.Day,
                period.Week?.Year,
                period.Week?.Week,
                period.Year,
                period.Month,
                period.Quarter);

        public PlannedPeriod ToDomain() =>
            Kind switch
            {
                PlannedPeriodKind.Unplanned => PlannedPeriod.Unplanned,
                PlannedPeriodKind.Day when Day is { } day => PlannedPeriod.ForDay(day),
                PlannedPeriodKind.Week when WeekYear is { } year && WeekNumber is { } week =>
                    PlannedPeriod.ForWeek(new IsoWeekKey(year, week)),
                PlannedPeriodKind.Month when Year is { } year && Month is { } month =>
                    PlannedPeriod.ForMonth(year, month),
                PlannedPeriodKind.Quarter when Year is { } year && Quarter is { } quarter =>
                    PlannedPeriod.ForQuarter(year, quarter),
                PlannedPeriodKind.Year when Year is { } year => PlannedPeriod.ForYear(year),
                _ => throw new JsonException("Journal state contains an invalid planned period.")
            };
    }
}
