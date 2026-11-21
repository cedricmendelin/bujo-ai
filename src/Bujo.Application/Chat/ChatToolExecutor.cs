using System.Security.Claims;
using System.Text.Json;
using Bujo.Domain;

namespace Bujo.Application.Chat;

public sealed class ChatToolExecutor(
    JournalApplicationService journal,
    ReviewApplicationService reviews) : IChatToolExecutor
{
    public ChatToolResult Execute(ChatToolRequest request)
    {
        try
        {
            var descriptor = ChatToolRegistry.All.SingleOrDefault(tool =>
                StringComparer.Ordinal.Equals(tool.Name, request.Name));
            if (descriptor is null)
            {
                return new ChatToolResult(false, $"Unknown chat tool: {request.Name}", null);
            }

            if (descriptor.RequiresApproval && request.Name != ChatToolNames.CreateReviewProposal)
            {
                return new ChatToolResult(false,
                    "This action requires an explicit approval or action in the journal UI.", null);
            }

            return request.Name switch
            {
                "get_journal" => Success("Journal snapshot loaded.", journal.GetSnapshot()),
                "get_daily_start" => Success(journal.GetDailyStart(GetString(request.Arguments, "timeZone"))),
                "get_upcoming" => Success(journal.GetUpcoming(
                    GetString(request.Arguments, "timeZone"),
                    GetInt(request.Arguments, "horizon", 14),
                    GetInt(request.Arguments, "page", 1),
                    GetInt(request.Arguments, "limit", 50),
                    GetBool(request.Arguments, "futureLog", false))),
                "find_lists" => FindLists(request),
                "resolve_occurrence" => Success(journal.Resolve(GetGuid(request.Arguments, "occurrenceId"))),
                "get_list_review" => Success(reviews.GetListReview(GetGuid(request.Arguments, "listId"))),
                "get_review_proposal" => Success(reviews.GetProposal(
                    GetGuid(request.Arguments, "proposalId"),
                    PwaActor(request.Principal, false))),
                "capture_item" => Capture(request),
                "create_list" => CreateList(request),
                "edit_item" => Success(journal.EditItem(
                    GetGuid(request.Arguments, "itemId"),
                    GetRequiredString(request.Arguments, "name"),
                    GetString(request.Arguments, "description"),
                    GetTags(request.Arguments))),
                "set_planned_period" => SetPlannedPeriod(request),
                "set_list_goal" => Success(journal.SetListGoal(
                    GetGuid(request.Arguments, "listId"),
                    GetString(request.Arguments, "goalText"))),
                "reopen_list" => Success(journal.ReopenList(GetGuid(request.Arguments, "listId"))),
                "complete_occurrence" => Success(journal.Transition(
                    GetGuid(request.Arguments, "occurrenceId"),
                    OccurrenceAction.Done)),
                "abandon_occurrence" => Success(journal.Transition(
                    GetGuid(request.Arguments, "occurrenceId"),
                    OccurrenceAction.Abandon)),
                "reopen_occurrence" => Success(journal.Transition(
                    GetGuid(request.Arguments, "occurrenceId"),
                    OccurrenceAction.Reopen)),
                "create_review_proposal" => CreateReviewProposal(request),
                _ => new ChatToolResult(false, $"Unknown chat tool: {request.Name}", null)
            };
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or DomainException)
        {
            return new ChatToolResult(false, exception.Message, null);
        }
    }

    private ChatToolResult Capture(ChatToolRequest request)
    {
        if (request.Arguments.ValueKind == JsonValueKind.Object &&
            request.Arguments.TryGetProperty("type", out var typeValue) &&
            typeValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return new ChatToolResult(false, CaptureItemType.InvalidMessage, null);
        }

        if (!CaptureItemType.TryParse(GetString(request.Arguments, "type"), out var type))
        {
            return new ChatToolResult(false, CaptureItemType.InvalidMessage, null);
        }

        var period = ParsePeriod(request.Arguments);
        if (!period.IsSuccess)
        {
            return Failure(period.Error!);
        }

        Guid? listId = TryGuid(request.Arguments, "listId");
        return Success(journal.Capture(
            GetRequiredString(request.Arguments, "name"),
            GetString(request.Arguments, "description"),
            GetTags(request.Arguments),
            period.Value,
            listId,
            type));
    }

    private ChatToolResult CreateList(ChatToolRequest request)
    {
        var name = GetRequiredString(request.Arguments, "name");
        var kindText = GetRequiredString(request.Arguments, "kind");
        if (!Enum.TryParse<JournalListKind>(kindText, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            return new ChatToolResult(false, "The list kind must be FutureLog, Weekly, Custom, Monthly, Quarterly, or Yearly.", null);
        }

        var weekKey = TryWeekKey(request.Arguments);
        if (kind == JournalListKind.Weekly)
        {
            if (weekKey is null)
            {
                return new ChatToolResult(false, "A weekly list requires a valid ISO year and week.", null);
            }
        }

        JournalPeriodKey? periodKey = null;
        if (request.Arguments.TryGetProperty("periodKey", out var rawPeriod) &&
            rawPeriod.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new ArgumentException("The journal period key is invalid.");
        if (TryGetObject(request.Arguments, "periodKey", out var period))
        {
            var periodKindText = GetRequiredString(period, "kind");
            if (!Enum.TryParse<JournalListKind>(periodKindText, true, out var periodKind))
                return new ChatToolResult(false, "The journal period kind is invalid.", null);
            periodKey = new JournalPeriodKey(periodKind, GetInt(period, "year", 0),
                TryInt(period, "month"), TryInt(period, "quarter"));
        }

        return Success(journal.CreateList(
            name,
            kind,
            GetString(request.Arguments, "goalText"),
            weekKey,
            periodKey));
    }

    private ChatToolResult SetPlannedPeriod(ChatToolRequest request)
    {
        var period = ParsePeriod(request.Arguments);
        return period.IsSuccess
            ? Success(journal.ChangePlannedPeriod(
                GetGuid(request.Arguments, "itemId"),
                period.Value!))
            : Failure(period.Error!);
    }

    private ChatToolResult FindLists(ChatToolRequest request)
    {
        var name = GetRequiredString(request.Arguments, "name").Trim();
        var matches = journal.GetSnapshot().Lists
            .Where(list => list.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            0 => new ChatToolResult(false, $"No journal lists match '{name}'.", matches),
            1 => Success("One matching list found.", matches[0]),
            _ => new ChatToolResult(false, "The list name is ambiguous. Use the list ID.", matches)
        };
    }

    private ChatToolResult CreateReviewProposal(ChatToolRequest request)
    {
        if (!TryGetArray(request.Arguments, "changes", out var changesElement))
        {
            return new ChatToolResult(false, "The proposal has no changes.", null);
        }

        var changes = new List<ReviewChangeRequest>();
        foreach (var changeElement in changesElement.EnumerateArray())
        {
            if (!Enum.TryParse<ReviewDisposition>(GetString(changeElement, "action") ?? "", true, out var action))
            {
                return new ChatToolResult(false, "A proposal action must be Move, Done, or Abandon.", null);
            }

            var itemId = GetGuid(changeElement, "itemId");
            var occurrenceId = GetGuid(changeElement, "occurrenceId");
            var expectedItemVersion = GetLong(changeElement, "expectedItemVersion");
            Guid? destinationListId = TryGuid(changeElement, "destinationListId");
            long? destinationVersion = TryLong(changeElement, "expectedDestinationListVersion");
            changes.Add(new ReviewChangeRequest(
                itemId,
                occurrenceId,
                expectedItemVersion,
                action,
                destinationListId,
                destinationVersion));
        }

        var actor = PwaActor(request.Principal, false);
        actor = actor with { Source = ReviewProposalSource.Ceremony };
        var result = reviews.CreateProposal(new ReviewProposalRequest(
            GetGuid(request.Arguments, "listId"),
            GetLong(request.Arguments, "expectedListVersion"),
            GetBool(request.Arguments, "requestCloseList", false),
            changes), actor);
        return Success(result);
    }

    private DomainResult<PlannedPeriod> ParsePeriod(JsonElement arguments)
    {
        if (!TryGetObject(arguments, "plannedPeriod", out var period))
        {
            return DomainResult<PlannedPeriod>.Success(PlannedPeriod.Unplanned);
        }

        var kind = GetString(period, "kind") ?? "unplanned";
        try
        {
            return kind.ToLowerInvariant() switch
            {
                "unplanned" => DomainResult<PlannedPeriod>.Success(PlannedPeriod.Unplanned),
                "day" => DomainResult<PlannedPeriod>.Success(PlannedPeriod.ForDay(
                    DateOnly.Parse(GetRequiredString(period, "day")))),
                "week" => DomainResult<PlannedPeriod>.Success(PlannedPeriod.ForWeek(
                    new IsoWeekKey(
                        GetInt(period, "year", 0),
                        GetInt(period, "week", 0)))),
                "month" => DomainResult<PlannedPeriod>.Success(PlannedPeriod.ForMonth(
                    GetInt(period, "year", 0),
                    GetInt(period, "month", 0))),
                "quarter" => DomainResult<PlannedPeriod>.Success(PlannedPeriod.ForQuarter(
                    GetInt(period, "year", 0),
                    GetInt(period, "quarter", 0))),
                "year" => DomainResult<PlannedPeriod>.Success(PlannedPeriod.ForYear(GetInt(period, "year", 0))),
                _ => DomainResult<PlannedPeriod>.Failure(new DomainError(
                    DomainErrorCode.InvalidInput,
                    "The planned-period kind must be unplanned, day, week, month, quarter, or year."))
            };
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return DomainResult<PlannedPeriod>.Failure(new DomainError(
                DomainErrorCode.InvalidInput,
                "The planned period is invalid."));
        }
    }

    private IsoWeekKey? TryWeekKey(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("weekKey", out var rawWeek) &&
            rawWeek.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new ArgumentException("The ISO week key is invalid.");

        if (!TryGetObject(arguments, "weekKey", out var weekKey))
        {
            return null;
        }

        var key = new IsoWeekKey(GetInt(weekKey, "year", 0), GetInt(weekKey, "week", 0));
        return key.IsValid ? key : null;
    }

    private static ChatToolResult Success<T>(DomainResult<T> result)
    {
        return result.IsSuccess
            ? Success("Operation completed.", result.Value)
            : Failure(result.Error!);
    }

    private static ChatToolResult Success(string summary, object? value) =>
        new(true, summary, value);

    private static ChatToolResult Failure(DomainError error) =>
        new(false, error.Message, null);

    private static ReviewActor PwaActor(ClaimsPrincipal principal, bool interactive)
    {
        return ReviewActor.FromPrincipal(principal, ReviewProposalSource.Pwa, interactive);
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string GetRequiredString(JsonElement element, string name) =>
        GetString(element, name) ?? throw new ArgumentException($"A '{name}' value is required.");

    private static Guid GetGuid(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return Guid.TryParse(text, out var id) ? id : throw new ArgumentException($"A valid '{name}' GUID is required.");
    }

    private static Guid? TryGuid(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return Guid.TryParse(text, out var id) ? id : null;
    }

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    private static int? TryInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        throw new ArgumentException($"A valid '{name}' integer is required.");
    }

    private static long GetLong(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var number)
            ? number
            : throw new ArgumentException($"A valid '{name}' integer is required.");
    }

    private static long? TryLong(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static bool GetBool(JsonElement element, string name, bool fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;
    }

    private static IReadOnlyList<string>? GetTags(JsonElement element)
    {
        if (!TryGetArray(element, "tags", out var tags))
        {
            return null;
        }

        return tags.EnumerateArray()
            .Where(tag => tag.ValueKind == JsonValueKind.String)
            .Select(tag => tag.GetString()!)
            .ToArray();
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Array;
    }
}
