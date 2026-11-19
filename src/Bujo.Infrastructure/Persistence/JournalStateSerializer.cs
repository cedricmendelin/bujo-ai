using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bujo.Domain;

namespace Bujo.Infrastructure.Persistence;

internal static class JournalStateSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string SerializeMoveResult(MoveResult result) =>
        JsonSerializer.Serialize(result, Options);

    public static MoveResult DeserializeMoveResult(string result) =>
        JsonSerializer.Deserialize<MoveResult>(result, Options)
        ?? throw new JsonException("Stored move result is empty.");

    public static string SerializeReviewChanges(IReadOnlyList<ReviewChange> changes) =>
        JsonSerializer.Serialize(changes, Options);

    public static IReadOnlyList<ReviewChange> DeserializeReviewChanges(string changes) =>
        JsonSerializer.Deserialize<ReviewChange[]>(changes, Options)
        ?? throw new JsonException("Stored review proposal changes are empty.");

    public static string SerializeKnowledgeBackup(KnowledgeBackupSnapshot backup) =>
        JsonSerializer.Serialize(backup, Options);

    public static KnowledgeBackupSnapshot DeserializeKnowledgeBackup(string backup) =>
        JsonSerializer.Deserialize<KnowledgeBackupSnapshot>(backup, Options)
        ?? throw new JsonException("Knowledge backup is empty.");

    private static JsonSerializerOptions CreateOptions()
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
}
