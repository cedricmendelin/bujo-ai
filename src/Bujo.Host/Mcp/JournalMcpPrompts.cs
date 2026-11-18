using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bujo.Host.Mcp;

[McpServerPromptType]
public sealed class JournalMcpPrompts
{
    [McpServerPrompt(Name = "capture_item"), Description("Guide for capturing one journal item without inferring a destination or relative date.")]
    public static string CaptureItem(
        [Description("The user's intended item text.")] string itemText) =>
        $"Capture exactly one item based on this request: {itemText}. Ask for clarification if a list or calendar period is ambiguous. Use a list ID, and pass an explicit ISO planned period; otherwise leave it unplanned in the Future Log.";

    [McpServerPrompt(Name = "daily_start"), Description("Guide for reviewing daily-start results without automatically changing the journal.")]
    public static string DailyStart(
        [Description("The user's IANA time zone.")] string timeZone) =>
        $"Use get_daily_start with the explicit IANA time zone '{timeZone}'. Summarize the returned groups and ask before making any change.";

    [McpServerPrompt(Name = "explicit_edit"), Description("Guide for applying only an explicit single-item edit.")]
    public static string ExplicitEdit(
        [Description("The exact change requested by the user.")] string requestedChange) =>
        $"Apply only this explicit single-item change: {requestedChange}. Resolve an item by stable ID, do not interpret relative dates, and do not perform bulk or destructive actions.";
}
