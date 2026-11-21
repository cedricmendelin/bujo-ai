using Bujo.Application;
using Bujo.Domain;
using System.Text.Json.Serialization;

namespace Bujo.Host;

public static class FollowUpEndpoints
{
    public static IEndpointRouteBuilder MapFollowUpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/followups").RequireAuthorization("ApprovedPrincipal");
        api.MapPost("", (CaptureFollowUpRequest request, FollowUpApplicationService followUps) =>
            request.DueAtUtc is { } due
                ? ToHttpResult(followUps.Capture(request.Title, request.Condition, due,
                    request.TimeZoneId, request.ItemId, request.Source))
                : Invalid("A due time is required."));
        api.MapGet("", (bool? due, FollowUpApplicationService followUps) =>
            due == true ? ToHttpResult(followUps.ListDue()) : Invalid("The due filter must be true."));
        api.MapGet("/{id:guid}", (Guid id, FollowUpApplicationService followUps) =>
            ToHttpResult(followUps.Get(id)));
        api.MapPost("/{id:guid}/evidence", (Guid id, RecordEvidenceRequest request, FollowUpApplicationService followUps) =>
            ToHttpResult(followUps.RecordEvidence(id, request.Source, request.Observation, request.CheckedScope)));
        api.MapPost("/{id:guid}/dismiss", (Guid id, FollowUpApplicationService followUps) =>
            ToHttpResult(followUps.Dismiss(id)));
        api.MapPost("/{id:guid}/defer", (Guid id, DeferFollowUpRequest request, FollowUpApplicationService followUps) =>
            request.DueAtUtc is { } due
                ? ToHttpResult(followUps.Defer(id, due))
                : Invalid("A due time is required."));
        api.MapPut("/{id:guid}/draft", (Guid id, FollowUpDraftRequest request, FollowUpApplicationService followUps) =>
            ToHttpResult(followUps.SetDraftIfCurrent(id, request.ExpectedDraft, request.Draft)));
        return endpoints;
    }

    private static IResult ToHttpResult<T>(DomainResult<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Error(result.Error!);

    private static IResult Invalid(string message) => Error(new DomainError(DomainErrorCode.InvalidInput, message));

    private static IResult Error(DomainError error) =>
        Results.Problem(
            statusCode: JournalContract.StatusCode(error.Code),
            title: JournalContract.ErrorCode(error.Code),
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = JournalContract.ErrorCode(error.Code) });

    public sealed record CaptureFollowUpRequest(
        string Title, string Condition, DateTimeOffset? DueAtUtc, string TimeZoneId, Guid? ItemId, EvidenceSource Source);

    public sealed record RecordEvidenceRequest(EvidenceSource Source, FollowUpObservation Observation, string? CheckedScope);
    public sealed record DeferFollowUpRequest(DateTimeOffset? DueAtUtc);
    public sealed record FollowUpDraftRequest(string? Draft, [property: JsonRequired] string? ExpectedDraft);
}
