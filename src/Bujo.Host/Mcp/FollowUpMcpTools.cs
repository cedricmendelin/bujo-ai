using System.ComponentModel;
using System.Text.Json;
using Bujo.Application;
using Bujo.Domain;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bujo.Host.Mcp;

[McpServerToolType]
public sealed class FollowUpMcpTools
{
    [McpServerTool(Name = "capture_followup"), Description(
        "Captures a conditional follow-up with an explicitly selected manual evidence source. Nothing is ever sent.")]
    public static CallToolResult CaptureFollowUp(
        FollowUpApplicationService followUps, string title, string condition, DateTimeOffset dueAtUtc,
        string timeZoneId, EvidenceSource source, Guid? itemId = null) =>
        McpErrors.From(followUps.Capture(title, condition, dueAtUtc, timeZoneId, itemId, source));

    [McpServerTool(Name = "list_due_followups", ReadOnly = true, Destructive = false), Description(
        "Lists pending follow-ups due now using the server clock. Nothing is ever sent.")]
    public static CallToolResult ListDueFollowUps(FollowUpApplicationService followUps) =>
        McpErrors.From(followUps.ListDue());

    [McpServerTool(Name = "get_followup", ReadOnly = true, Destructive = false), Description(
        "Reviews a follow-up and its derived evidence outcome. Unavailable evidence is not proof of no reply. Nothing is ever sent.")]
    public static CallToolResult GetFollowUp(FollowUpApplicationService followUps, Guid id) =>
        McpErrors.From(followUps.Get(id));

    [McpServerTool(Name = "record_followup_evidence"), Description(
        "Appends a manual observation or correction; no reply requires a checked scope for that outcome. Nothing is ever sent.")]
    public static CallToolResult RecordFollowUpEvidence(
        FollowUpApplicationService followUps, Guid id, EvidenceSource source,
        FollowUpObservation observation, string? checkedScope = null) =>
        McpErrors.From(followUps.RecordEvidence(id, source, observation, checkedScope));

    [McpServerTool(Name = "dismiss_followup"), Description(
        "Dismisses one follow-up by explicit user action. Nothing is ever sent.")]
    public static CallToolResult DismissFollowUp(FollowUpApplicationService followUps, Guid id) =>
        McpErrors.From(followUps.Dismiss(id));

    [McpServerTool(Name = "defer_followup"), Description(
        "Defers one follow-up to an explicitly chosen due time. Nothing is ever sent.")]
    public static CallToolResult DeferFollowUp(FollowUpApplicationService followUps, Guid id, DateTimeOffset dueAtUtc) =>
        McpErrors.From(followUps.Defer(id, dueAtUtc));

    [McpServerTool(Name = "set_followup_draft"), Description(
        "Stores or clears user-authored plain text for review or copying only if expectedDraft matches the current draft. Supply expectedDraft explicitly: null means no draft. A stale value returns a conflict. Nothing is ever sent.")]
    public static CallToolResult SetFollowUpDraft(
        FollowUpApplicationService followUps, Guid id, string? draft,
        [Description("Required current draft as a string, or null if no draft exists.")] JsonElement expectedDraft) =>
        expectedDraft.ValueKind switch
        {
            JsonValueKind.Null => McpErrors.From(followUps.SetDraftIfCurrent(id, null, draft)),
            JsonValueKind.String => McpErrors.From(followUps.SetDraftIfCurrent(id, expectedDraft.GetString(), draft)),
            _ => McpErrors.Error(DomainErrorCode.InvalidInput, "The expected draft must be supplied as a string or null.")
        };
}
