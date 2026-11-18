using System.ComponentModel;
using System.Security.Claims;
using Bujo.Application;
using Bujo.Domain;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bujo.Host.Mcp;

[McpServerToolType]
public sealed class JournalMcpTools
{
    [McpServerTool(Name = "get_journal", ReadOnly = true, Destructive = false), Description("Returns journal items, lists, occurrences, and move history with stable IDs.")]
    public static CallToolResult GetJournal(JournalApplicationService journal) =>
        McpErrors.Success(journal.GetSnapshot());

    [McpServerTool(Name = "get_daily_start", ReadOnly = true, Destructive = false), Description("Returns the journal daily-start groups in the supplied IANA time zone.")]
    public static CallToolResult GetDailyStart(
        JournalApplicationService journal,
        [Description("Required IANA time zone, such as Europe/Zurich.")] string timeZone) =>
        McpErrors.From(journal.GetDailyStart(timeZone));

    [McpServerTool(Name = "get_upcoming", ReadOnly = true, Destructive = false), Description("Returns planned items overlapping the requested local-date horizon.")]
    public static CallToolResult GetUpcoming(
        JournalApplicationService journal,
        [Description("Required IANA time zone, such as Europe/Zurich.")] string timeZone,
        [Description("Number of calendar days in the horizon.")] int horizon = 14,
        [Description("One-based page number.")] int page = 1,
        [Description("Maximum results per page.")] int limit = 50,
        [Description("Include Future Log items.")] bool futureLog = false) =>
        McpErrors.From(journal.GetUpcoming(timeZone, horizon, page, limit, futureLog));

    [McpServerTool(Name = "find_lists", ReadOnly = true, Destructive = false), Description("Finds matching journal lists. Use a returned list ID for writes; ambiguous matches are errors, never silently selected.")]
    public static CallToolResult FindLists(
        JournalApplicationService journal,
        [Description("List name or fragment to search for.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return McpErrors.Error(DomainErrorCode.InvalidInput, "A list name is required.");
        }

        var candidates = journal.GetSnapshot().Lists
            .Where(list => list.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return candidates.Length switch
        {
            0 => McpErrors.Error("NotFound", $"No journal lists match '{name}'.", candidates),
            1 => McpErrors.Success(candidates),
            _ => McpErrors.Error("AmbiguousList", "The list name matches multiple candidates. Use a list ID.", candidates)
        };
    }

    [McpServerTool(Name = "capture_item"), Description("Captures one item in the specified list or Future Log. Omitted plannedPeriod means unplanned; dates must be explicit, not relative.")]
    public static CallToolResult CaptureItem(
        JournalApplicationService journal,
        string name,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        JournalEndpoints.PeriodRequest? plannedPeriod = null,
        Guid? listId = null)
    {
        var period = JournalContract.ParsePeriod(plannedPeriod);
        return period.IsSuccess
            ? McpErrors.From(journal.Capture(name, description, tags, period.Value, listId))
            : McpErrors.Error(period.Error!);
    }

    [McpServerTool(Name = "create_list"), Description("Creates one journal list.")]
    public static CallToolResult CreateList(
        JournalApplicationService journal,
        string name,
        JournalListKind kind,
        string? goalText = null,
        JournalEndpoints.WeekKeyRequest? weekKey = null)
    {
        var parsedWeekKey = JournalContract.ParseWeekKey(weekKey);
        return parsedWeekKey.IsSuccess
            ? McpErrors.From(journal.CreateList(name, kind, goalText, parsedWeekKey.Value))
            : McpErrors.Error(parsedWeekKey.Error!);
    }

    [McpServerTool(Name = "resolve_occurrence", ReadOnly = true, Destructive = false), Description("Returns the selected occurrence's full chain and current destination/state.")]
    public static CallToolResult ResolveOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Resolve(occurrenceId));

    [McpServerTool(Name = "edit_item"), Description("Explicitly edits one item's name, description, and tags.")]
    public static CallToolResult EditItem(
        JournalApplicationService journal,
        Guid itemId,
        string name,
        string? description = null,
        IReadOnlyList<string>? tags = null) =>
        McpErrors.From(journal.EditItem(itemId, name, description, tags));

    [McpServerTool(Name = "set_planned_period"), Description("Sets one item's planned period using explicit ISO calendar values; relative dates are not interpreted.")]
    public static CallToolResult SetPlannedPeriod(
        JournalApplicationService journal,
        Guid itemId,
        JournalEndpoints.PeriodRequest plannedPeriod)
    {
        var period = JournalContract.ParsePeriod(plannedPeriod);
        return period.IsSuccess
            ? McpErrors.From(journal.ChangePlannedPeriod(itemId, period.Value!))
            : McpErrors.Error(period.Error!);
    }

    [McpServerTool(Name = "set_list_goal"), Description("Sets or clears the goal statement for one list.")]
    public static CallToolResult SetListGoal(
        JournalApplicationService journal,
        Guid listId,
        string? goalText) =>
        McpErrors.From(journal.SetListGoal(listId, goalText));

    [McpServerTool(Name = "close_list"), Description("Closes a list only when it has no unfinished occurrences. Use get_list_review and an approved review proposal to apply dispositions before closure. The Future Log cannot be closed.")]
    public static CallToolResult CloseList(JournalApplicationService journal, Guid listId) =>
        McpErrors.From(journal.CloseList(listId));

    [McpServerTool(Name = "reopen_list"), Description("Reopens one closed journal list. Occurrences on the list remain in their prior states and must be explicitly acted on.")]
    public static CallToolResult ReopenList(JournalApplicationService journal, Guid listId) =>
        McpErrors.From(journal.ReopenList(listId));

    [McpServerTool(Name = "get_list_review", ReadOnly = true, Destructive = false),
     Description("Returns unfinished occurrences, historical moved links with their resolved destination/state, and resolved history as separate sections.")]
    public static CallToolResult GetListReview(ReviewApplicationService review, Guid listId) =>
        McpErrors.From(review.GetListReview(listId));

    [McpServerTool(Name = "create_review_proposal"),
     Description("Creates an inspectable, immutable list-review proposal. Include exact occurrence and item versions, destinations and whether the list should close. Approval is only available in the PWA.")]
    public static CallToolResult CreateReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        ReviewProposalRequest request) =>
        McpErrors.From(review.CreateProposal(
            request,
            Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = "get_review_proposal", ReadOnly = true, Destructive = false),
     Description("Returns a proposal created by the authenticated user, including its exact changes and digest.")]
    public static CallToolResult GetReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.GetProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = "cancel_review_proposal"),
     Description("Cancels a pending or approved proposal created by the authenticated user without changing journal data.")]
    public static CallToolResult CancelReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.CancelProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = "reject_review_proposal"),
     Description("Rejects a pending or approved proposal created by the authenticated user without changing journal data.")]
    public static CallToolResult RejectReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.RejectProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = "apply_review_proposal"),
     Description("Applies a proposal only after its creator has approved its digest in the authenticated PWA. No approval argument is accepted.")]
    public static CallToolResult ApplyReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.ApplyProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = "move_item", Idempotent = true), Description("Moves one item to a list. Reuse operationId on retries and supply the expected current occurrence ID.")]
    public static CallToolResult MoveItem(
        JournalApplicationService journal,
        Guid operationId,
        Guid itemId,
        Guid expectedOccurrenceId,
        Guid destinationListId) =>
        McpErrors.From(journal.Move(operationId, itemId, expectedOccurrenceId, destinationListId));

    [McpServerTool(Name = "complete_occurrence"), Description("Marks one occurrence done.")]
    public static CallToolResult CompleteOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Transition(occurrenceId, OccurrenceAction.Done));

    [McpServerTool(Name = "abandon_occurrence"), Description("Marks one occurrence abandoned.")]
    public static CallToolResult AbandonOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Transition(occurrenceId, OccurrenceAction.Abandon));

    [McpServerTool(Name = "reopen_occurrence"), Description("Reopens one eligible current occurrence.")]
    public static CallToolResult ReopenOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Transition(occurrenceId, OccurrenceAction.Reopen));

    private static ReviewActor Actor(IHttpContextAccessor accessor, ReviewProposalSource source) =>
        ReviewActor.FromPrincipal(accessor.HttpContext?.User ?? new ClaimsPrincipal(), source, interactive: false);
}
