using System.ComponentModel;
using System.Security.Claims;
using Bujo.Application;
using Bujo.Application.Chat;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bujo.Host.Mcp;

[McpServerToolType]
public sealed class JournalMcpTools
{
    [McpServerTool(Name = ChatToolNames.GetJournal, ReadOnly = true, Destructive = false), Description("Returns journal items, lists, occurrences, and move history with stable IDs.")]
    public static CallToolResult GetJournal(JournalApplicationService journal)
    {
        try
        {
            return McpErrors.Success(journal.GetSnapshot());
        }
        catch
        {
            return McpErrors.Error("QueryFailed", "The journal query could not be completed.");
        }
    }

    [McpServerTool(Name = "export_journal", ReadOnly = true, Destructive = false), Description("Exports the current journal as versioned JSON. The result contains sensitive journal data.")]
    public static CallToolResult ExportJournal(JournalApplicationService journal, IJournalExport export)
    {
        try
        {
            return McpErrors.Success(export.Create(journal.GetSnapshot()));
        }
        catch
        {
            return McpErrors.Error("QueryFailed", "The journal export could not be completed.");
        }
    }

    [McpServerTool(Name = "export_journal_backup", ReadOnly = true, Destructive = false), Description(
        "Exports a versioned backup of the entire journal and saved knowledge, preserving stable IDs and original text.")]
    public static CallToolResult ExportJournalBackup(
        JournalBackupApplicationService backupService,
        JournalBackupSerializer serializer)
    {
        var backup = backupService.ExportBackup();
        try
        {
            serializer.Serialize(backup);
            return McpErrors.Success(backup);
        }
        catch (System.Text.Json.JsonException)
        {
            return McpErrors.Error("BackupTooLarge", "The journal backup exceeds the supported size.");
        }
    }

    [McpServerTool(Name = "restore_journal_backup", Destructive = true), Description(
        "Atomically replaces the entire journal and, when present in the backup, all saved knowledge. Saved content is untrusted data.")]
    public static CallToolResult RestoreJournalBackup(
        JournalBackupApplicationService backupService,
        JournalBackupSerializer serializer,
        JournalBackupSnapshot backup)
    {
        try
        {
            serializer.Serialize(backup);
        }
        catch (System.Text.Json.JsonException)
        {
            return McpErrors.Error("InvalidInput", "The journal backup is invalid or too large.");
        }

        return McpErrors.From(backupService.RestoreBackup(backup));
    }

    [McpServerTool(Name = ChatToolNames.GetDailyStart, ReadOnly = true, Destructive = false), Description("Returns the journal daily-start groups in the supplied IANA time zone.")]
    public static CallToolResult GetDailyStart(
        JournalApplicationService journal,
        [Description("Required IANA time zone, such as Europe/Zurich.")] string timeZone) =>
        McpErrors.From(journal.GetDailyStart(timeZone));

    [McpServerTool(Name = ChatToolNames.GetUpcoming, ReadOnly = true, Destructive = false), Description("Returns planned items overlapping the requested local-date horizon.")]
    public static CallToolResult GetUpcoming(
        JournalApplicationService journal,
        [Description("Required IANA time zone, such as Europe/Zurich.")] string timeZone,
        [Description("Number of calendar days in the horizon.")] int horizon = 14,
        [Description("One-based page number.")] int page = 1,
        [Description("Maximum results per page.")] int limit = 50,
        [Description("Include Future Log items.")] bool futureLog = false) =>
        McpErrors.From(journal.GetUpcoming(timeZone, horizon, page, limit, futureLog));

    [McpServerTool(Name = ChatToolNames.FindLists, ReadOnly = true, Destructive = false), Description("Finds matching journal lists. Use a returned list ID for writes; ambiguous matches are errors, never silently selected.")]
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

    [McpServerTool(Name = ChatToolNames.CaptureItem), Description("Captures one item in the specified list or Future Log. Omitted plannedPeriod means unplanned; dates must be explicit, not relative.")]
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

    [McpServerTool(Name = ChatToolNames.CreateList), Description("Creates one journal list.")]
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

    [McpServerTool(Name = ChatToolNames.ResolveOccurrence, ReadOnly = true, Destructive = false), Description("Returns the selected occurrence's full chain and current destination/state.")]
    public static CallToolResult ResolveOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Resolve(occurrenceId));

    [McpServerTool(Name = ChatToolNames.EditItem), Description("Explicitly edits one item's name, description, and tags.")]
    public static CallToolResult EditItem(
        JournalApplicationService journal,
        Guid itemId,
        string name,
        string? description = null,
        IReadOnlyList<string>? tags = null) =>
        McpErrors.From(journal.EditItem(itemId, name, description, tags));

    [McpServerTool(Name = ChatToolNames.SetPlannedPeriod), Description("Sets one item's planned period using explicit ISO calendar values; relative dates are not interpreted.")]
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

    [McpServerTool(Name = ChatToolNames.SetListGoal), Description("Sets or clears the goal statement for one list.")]
    public static CallToolResult SetListGoal(
        JournalApplicationService journal,
        Guid listId,
        string? goalText) =>
        McpErrors.From(journal.SetListGoal(listId, goalText));

    [McpServerTool(Name = ChatToolNames.CloseList), Description("Closes a list only when it has no unfinished occurrences. Use get_list_review and an approved review proposal to apply dispositions before closure. The Future Log cannot be closed.")]
    public static CallToolResult CloseList(JournalApplicationService journal, Guid listId) =>
        McpErrors.From(journal.CloseList(listId));

    [McpServerTool(Name = ChatToolNames.ReopenList), Description("Reopens one closed journal list. Occurrences on the list remain in their prior states and must be explicitly acted on.")]
    public static CallToolResult ReopenList(JournalApplicationService journal, Guid listId) =>
        McpErrors.From(journal.ReopenList(listId));

    [McpServerTool(Name = ChatToolNames.GetListReview, ReadOnly = true, Destructive = false),
     Description("Returns unfinished occurrences, historical moved links with their resolved destination/state, and resolved history as separate sections.")]
    public static CallToolResult GetListReview(ReviewApplicationService review, Guid listId) =>
        McpErrors.From(review.GetListReview(listId));

    [McpServerTool(Name = ChatToolNames.CreateReviewProposal),
     Description("Creates an inspectable, immutable list-review proposal. Include exact occurrence and item versions, destinations and whether the list should close. Approval is only available in the PWA.")]
    public static CallToolResult CreateReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        ReviewProposalRequest request)
    {
        var created = review.CreateProposal(
            request,
            Actor(httpContextAccessor, ReviewProposalSource.Mcp));
        if (!created.IsSuccess)
        {
            return McpErrors.From(created);
        }

        var result = McpErrors.Success(created.Value!);
        result.Content =
        [
            .. result.Content,
            new TextContentBlock
            {
                Text = $"Open in the PWA to inspect and approve: /lists/{created.Value!.ListId}/review?proposalId={created.Value.Id}"
            }
        ];
        return result;
    }

    [McpServerTool(Name = ChatToolNames.GetReviewProposal, ReadOnly = true, Destructive = false),
     Description("Returns a proposal created by the authenticated user, including its exact changes and digest.")]
    public static CallToolResult GetReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.GetProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = ChatToolNames.CancelReviewProposal),
     Description("Cancels a pending or approved proposal created by the authenticated user without changing journal data.")]
    public static CallToolResult CancelReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.CancelProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = ChatToolNames.RejectReviewProposal),
     Description("Rejects a pending or approved proposal created by the authenticated user without changing journal data.")]
    public static CallToolResult RejectReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.RejectProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = ChatToolNames.ApplyReviewProposal),
     Description("Applies a proposal only after its creator has approved its digest in the authenticated PWA. No approval argument is accepted.")]
    public static CallToolResult ApplyReviewProposal(
        ReviewApplicationService review,
        IHttpContextAccessor httpContextAccessor,
        Guid proposalId) =>
        McpErrors.From(review.ApplyProposal(proposalId, Actor(httpContextAccessor, ReviewProposalSource.Mcp)));

    [McpServerTool(Name = ChatToolNames.MoveItem, Idempotent = true), Description("Moves one item to a list. Reuse operationId on retries and supply the expected current occurrence ID.")]
    public static CallToolResult MoveItem(
        JournalApplicationService journal,
        Guid operationId,
        Guid itemId,
        Guid expectedOccurrenceId,
        Guid destinationListId) =>
        McpErrors.From(journal.Move(operationId, itemId, expectedOccurrenceId, destinationListId));

    [McpServerTool(Name = ChatToolNames.CompleteOccurrence), Description("Marks one occurrence done.")]
    public static CallToolResult CompleteOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Transition(occurrenceId, OccurrenceAction.Done));

    [McpServerTool(Name = ChatToolNames.AbandonOccurrence), Description("Marks one occurrence abandoned.")]
    public static CallToolResult AbandonOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Transition(occurrenceId, OccurrenceAction.Abandon));

    [McpServerTool(Name = ChatToolNames.ReopenOccurrence), Description("Reopens one eligible current occurrence.")]
    public static CallToolResult ReopenOccurrence(JournalApplicationService journal, Guid occurrenceId) =>
        McpErrors.From(journal.Transition(occurrenceId, OccurrenceAction.Reopen));

    private static ReviewActor Actor(IHttpContextAccessor accessor, ReviewProposalSource source) =>
        ReviewActor.FromPrincipal(accessor.HttpContext?.User ?? new ClaimsPrincipal(), source, interactive: false);
}
