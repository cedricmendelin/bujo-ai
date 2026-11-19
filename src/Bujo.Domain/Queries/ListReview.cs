namespace Bujo.Domain;

public sealed record ListReviewEntry(
    ItemSnapshot Item,
    OccurrenceSnapshot Occurrence,
    ItemResolution? Resolution);

public sealed record ListReview(
    JournalListSnapshot List,
    IReadOnlyList<ListReviewEntry> Unfinished,
    IReadOnlyList<ListReviewEntry> Moved,
    IReadOnlyList<ListReviewEntry> Resolved);

public enum ReviewProposalSource
{
    Pwa,
    Mcp,
    Ceremony
}

public enum ReviewProposalStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
    Expired,
    Applied,
    Conflicted
}

public enum ReviewDisposition
{
    Move,
    Done,
    Abandon
}

public sealed record ReviewChange(
    Guid ItemId,
    Guid OccurrenceId,
    long ExpectedItemVersion,
    ReviewDisposition Action,
    Guid? DestinationListId = null,
    long? ExpectedDestinationListVersion = null,
    Guid OperationId = default);

public sealed record ReviewProposalSnapshot(
    Guid Id,
    Guid ListId,
    long ExpectedListVersion,
    string CreatedBy,
    ReviewProposalSource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ReviewProposalStatus Status,
    bool RequestCloseList,
    IReadOnlyList<ReviewChange> Changes,
    string Digest,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ApprovedDigest,
    DateTimeOffset? AppliedAt);

public sealed record CreateReviewProposalCommand(
    Guid Id,
    Guid ListId,
    long ExpectedListVersion,
    string CreatedBy,
    ReviewProposalSource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool RequestCloseList,
    IReadOnlyList<ReviewChange> Changes,
    string Digest);

public sealed record ReviewProposalStatusCommand(
    Guid Id,
    ReviewProposalStatus Status,
    string UserId,
    string? Digest,
    DateTimeOffset OccurredAt);

public sealed record ApplyReviewProposalCommand(
    Guid Id,
    string UserId,
    string Digest,
    DateTimeOffset OccurredAt);
