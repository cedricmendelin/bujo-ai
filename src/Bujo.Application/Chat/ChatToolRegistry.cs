using System.Text.Json;

namespace Bujo.Application.Chat;

public static class ChatToolNames
{
    public const string GetJournal = "get_journal";
    public const string GetDailyStart = "get_daily_start";
    public const string GetUpcoming = "get_upcoming";
    public const string FindLists = "find_lists";
    public const string CaptureItem = "capture_item";
    public const string CreateList = "create_list";
    public const string ResolveOccurrence = "resolve_occurrence";
    public const string EditItem = "edit_item";
    public const string SetPlannedPeriod = "set_planned_period";
    public const string SetListGoal = "set_list_goal";
    public const string CloseList = "close_list";
    public const string ReopenList = "reopen_list";
    public const string GetListReview = "get_list_review";
    public const string CreateReviewProposal = "create_review_proposal";
    public const string GetReviewProposal = "get_review_proposal";
    public const string CancelReviewProposal = "cancel_review_proposal";
    public const string RejectReviewProposal = "reject_review_proposal";
    public const string ApplyReviewProposal = "apply_review_proposal";
    public const string MoveItem = "move_item";
    public const string CompleteOccurrence = "complete_occurrence";
    public const string AbandonOccurrence = "abandon_occurrence";
    public const string ReopenOccurrence = "reopen_occurrence";
}

public sealed record ChatToolParameter(
    string Name,
    string JsonType,
    string Description,
    bool Required);

public sealed record ChatToolDescriptor(
    string Name,
    string Category,
    string Description,
    string ApiEndpoint,
    bool RequiresApproval,
    IReadOnlyList<ChatToolParameter> Parameters);

public static class ChatToolRegistry
{
    public static IReadOnlyList<ChatToolDescriptor> All { get; } =
    [
        Read("get_journal", "GET /api/journal", "Returns the complete journal snapshot."),
        Read("get_daily_start", "GET /api/daily-start", "Returns today's journal review.", ["timeZone"]),
        Read("get_upcoming", "GET /api/planning/upcoming", "Returns upcoming planned items.", ["timeZone"]),
        Read("find_lists", "/api/lists", "Finds journal lists by name.", ["name"]),
        Read("resolve_occurrence", "GET /api/occurrences/{id}/resolution", "Resolves an occurrence chain.", ["occurrenceId"]),
        Read("get_list_review", "GET /api/lists/{id}/review", "Returns unfinished list-review entries.", ["listId"]),
        Read("get_review_proposal", "GET /api/review-proposals/{id}", "Returns an owner-bound review proposal.", ["proposalId"]),

        Write("capture_item", "POST /api/items", "Captures an item.", false,
            ["name", "description?", "tags?", "plannedPeriod?", "listId?"]),
        Write("create_list", "POST /api/lists", "Creates a journal list.", false,
            ["name", "kind", "goalText?", "weekKey?"]),
        Write("edit_item", "PUT /api/items/{id}", "Explicitly edits an item.", false,
            ["itemId", "name", "description?", "tags?"]),
        Write("set_planned_period", "PUT /api/items/{id}/planned-period", "Changes an item's planned period.", false,
            ["itemId", "plannedPeriod"]),
        Write("set_list_goal", "PUT /api/lists/{id}/goal", "Sets or clears a list goal.", false,
            ["listId", "goalText"]),
        Write("close_list", "POST /api/lists/{id}/close", "Closes a list.", true, ["listId"]),
        Write("reopen_list", "POST /api/lists/{id}/reopen", "Reopens a list.", false, ["listId"]),
        Write("move_item", "POST /api/items/{id}/move", "Moves an item to another list.", true,
            ["operationId", "itemId", "expectedOccurrenceId", "destinationListId"]),
        Write("complete_occurrence", "POST /api/occurrences/{id}/transition", "Marks an occurrence done.", false,
            ["occurrenceId"]),
        Write("abandon_occurrence", "POST /api/occurrences/{id}/transition", "Marks an occurrence abandoned.", true,
            ["occurrenceId"]),
        Write("reopen_occurrence", "POST /api/occurrences/{id}/transition", "Reopens an occurrence.", false,
            ["occurrenceId"]),

        Write("create_review_proposal", "POST /api/review-proposals", "Creates an approval-bound review proposal.", true,
            ["listId", "expectedListVersion", "requestCloseList", "changes"]),
        Write("cancel_review_proposal", "POST /api/review-proposals/{id}/cancel", "Cancels a pending proposal.", true,
            ["proposalId"]),
        Write("reject_review_proposal", "POST /api/review-proposals/{id}/reject", "Rejects a proposal.", true,
            ["proposalId"]),
        Write("apply_review_proposal", "POST /api/review-proposals/{id}/apply", "Applies an explicitly approved proposal.", true,
            ["proposalId"])
    ];

    public static ChatToolDescriptor Get(string name) =>
        All.Single(tool => StringComparer.Ordinal.Equals(tool.Name, name));

    public static JsonElement ToJsonSchema(ChatToolDescriptor tool)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var parameter in tool.Parameters)
        {
            var name = parameter.Name.TrimEnd('?');
            properties[name] = new
            {
                type = parameter.JsonType,
                description = parameter.Description
            };
            if (parameter.Required)
            {
                required.Add(name);
            }
        }

        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties,
            required
        });
    }

    private static ChatToolDescriptor Read(string name, string endpoint, string description, string[]? parameters = null)
    {
        var descriptorParameters = parameters is null
            ? []
            : parameters.Select(parameter => Parameter(parameter)).ToArray();
        return new ChatToolDescriptor(name, "read", description, endpoint, false, descriptorParameters);
    }

    private static ChatToolDescriptor Write(
        string name,
        string endpoint,
        string description,
        bool requiresApproval,
        string[] parameters)
    {
        return new ChatToolDescriptor(
            name,
            "write",
            description,
            endpoint,
            requiresApproval,
            parameters.Select(Parameter).ToArray());
    }

    private static ChatToolParameter Parameter(string text)
    {
        var optional = text.EndsWith('?', StringComparison.Ordinal);
        var name = text.TrimEnd('?');
        return new ChatToolParameter(name, InferJsonType(name), $"{name} input.", !optional);
    }

    private static string InferJsonType(string name) => name switch
    {
        "itemId" or "listId" or "occurrenceId" or "proposalId" or "operationId" or "expectedOccurrenceId" or "destinationListId" =>
            "string",
        "horizon" or "page" or "limit" or "expectedListVersion" or "expectedItemVersion" or "expectedDestinationListVersion" or
            "year" or "week" =>
            "integer",
        "futureLog" or "requestCloseList" =>
            "boolean",
        "tags" or "changes" =>
            "array",
        "plannedPeriod" or "weekKey" =>
            "object",
        _ => "string"
    };
}
