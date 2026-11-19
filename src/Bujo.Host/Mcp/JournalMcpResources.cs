using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Bujo.Host.Mcp;

[McpServerResourceType]
public sealed class JournalMcpResources
{
    [McpServerResource(
        UriTemplate = "bujo://journal/capabilities",
        Name = "Journal capabilities",
        MimeType = "text/markdown")]
    [Description("Describes journal MCP capabilities, limitations, and stable tool error codes.")]
    public static string GetCapabilities() =>
        """
        # Journal MCP capabilities

        Read tools: get_journal, export_journal, get_daily_start, get_upcoming, find_lists,
        resolve_occurrence, get_list_review, get_review_proposal.
        Writes: capture_item, create_list, edit_item, set_planned_period, set_list_goal,
        close_list, reopen_list, move_item, complete_occurrence, abandon_occurrence,
        reopen_occurrence, create_review_proposal, cancel_review_proposal,
        reject_review_proposal, apply_review_proposal.

        Writes use the shared journal application service. Use stable list, item, and
        occurrence IDs. move_item requires a reusable operationId and the expected
        current occurrence ID. Name searches return candidates and never select a
        destination implicitly. Date values must be explicit ISO calendar periods;
        daily-start and upcoming queries require an IANA time zone.

        get_list_review separates unfinished occurrences, historical moved links
        with their resolved final destination/state, and resolved history. Bulk
        dispositions and requested closure use immutable review proposals tied to
        exact occurrence/item versions, destinations, and a server-computed digest.
        MCP can create, inspect, cancel, reject, and apply proposals, but cannot
        approve them. The creator must approve the exact digest in the authenticated
        interactive PWA before apply; an MCP argument cannot assert approval.
        close_list requires a list ID, rejects the Future Log, and requires all open
        occurrences to be resolved. reopen_list reopens a closed list by ID without
        changing occurrence states. The server does not interpret relative dates,
        run schedules, or connect to mail or calendars. Prompts are reusable
        templates only; they do not enable client scheduling or grant write authority.

        Tool errors are JSON with code and message, with candidates for list
        lookup errors. Domain codes: InvalidInput, InvalidTimeZone, NotFound,
        InvalidTransition, ListClosed, StaleOccurrence, ConcurrencyConflict,
        IdempotencyKeyReuse, DuplicateFutureLog, DuplicateListName,
        InvariantViolation, UnresolvedOccurrences, FutureLogCannotClose.
        Adapter lookup codes: AmbiguousList.
        """;
}
