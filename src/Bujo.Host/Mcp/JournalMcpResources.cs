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

        Read tools: get_journal, get_daily_start, get_upcoming, find_lists, resolve_occurrence.
        Single-item writes: capture_item, create_list, edit_item, set_planned_period,
        set_list_goal, move_item, complete_occurrence, abandon_occurrence, reopen_occurrence.

        Writes use the shared journal application service. Use stable list, item, and
        occurrence IDs. move_item requires a reusable operationId and the expected
        current occurrence ID. Name searches return candidates and never select a
        destination implicitly. Date values must be explicit ISO calendar periods;
        daily-start and upcoming queries require an IANA time zone.

        The server does not interpret relative dates, perform bulk/list-closure
        operations, apply ceremony proposals, run schedules, or connect to mail or
        calendars. Prompts are reusable templates only; they do not enable client
        scheduling or grant write authority.

        Tool errors are JSON with code and message, with candidates for list
        lookup errors. Domain codes: InvalidInput, InvalidTimeZone, NotFound,
        InvalidTransition, ListClosed, StaleOccurrence, ConcurrencyConflict,
        IdempotencyKeyReuse, DuplicateFutureLog, InvariantViolation,
        UnresolvedOccurrences, FutureLogCannotClose.
        Adapter lookup codes: AmbiguousList.
        """;
}
