using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Bujo.Domain;

namespace Bujo.Application;

public sealed record ReviewActor(string UserId, ReviewProposalSource Source, bool Interactive)
{
    public static ReviewActor FromPrincipal(
        ClaimsPrincipal principal,
        ReviewProposalSource source,
        bool interactive)
    {
        var tenant = principal.FindFirst("tid")?.Value;
        var objectId = principal.FindFirst("oid")?.Value;
        var userId = Guid.TryParse(tenant, out var tenantId) && tenantId != Guid.Empty &&
                     Guid.TryParse(objectId, out var parsedObjectId) && parsedObjectId != Guid.Empty
            ? $"{tenantId:D}:{parsedObjectId:D}"
            : string.Empty;
        return new ReviewActor(userId, source, interactive);
    }
}

public sealed record ReviewProposalRequest(
    Guid ListId,
    long ExpectedListVersion,
    bool RequestCloseList,
    IReadOnlyList<ReviewChangeRequest> Changes);

public sealed record ReviewChangeRequest(
    Guid ItemId,
    Guid OccurrenceId,
    long ExpectedItemVersion,
    ReviewDisposition Action,
    Guid? DestinationListId = null,
    long? ExpectedDestinationListVersion = null);

public sealed class ReviewApplicationService(IJournalRepository repository, TimeProvider? clock = null)
{
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromHours(24);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public DomainResult<ListReview> GetListReview(Guid listId) =>
        repository.GetSnapshot().GetListReview(listId);

    public DomainResult<ReviewProposalSnapshot> CreateProposal(
        ReviewProposalRequest request,
        ReviewActor actor)
    {
        var actorValidation = ValidateActor(actor);
        if (actorValidation is not null)
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(actorValidation);
        }

        if (!Enum.IsDefined(actor.Source) || request.ListId == Guid.Empty ||
            request.Changes is null || request.Changes.Count == 0)
        {
            return InvalidProposal();
        }

        var snapshot = repository.GetSnapshot();
        var review = snapshot.GetListReview(request.ListId);
        if (!review.IsSuccess)
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(review.Error!);
        }

        if (review.Value!.List.State != JournalListState.Open ||
            review.Value.List.Version != request.ExpectedListVersion)
        {
            return Conflict<ReviewProposalSnapshot>("The list changed since the review was opened.");
        }

        var seenItems = new HashSet<Guid>();
        var seenOccurrences = new HashSet<Guid>();
        var changes = new List<ReviewChange>(request.Changes.Count);
        foreach (var requestedChange in request.Changes)
        {
            if (!Enum.IsDefined(requestedChange.Action) ||
                requestedChange.ItemId == Guid.Empty ||
                requestedChange.OccurrenceId == Guid.Empty ||
                !seenItems.Add(requestedChange.ItemId) ||
                !seenOccurrences.Add(requestedChange.OccurrenceId))
            {
                return InvalidProposal();
            }

            var item = snapshot.Items.SingleOrDefault(candidate => candidate.Id == requestedChange.ItemId);
            var occurrence = snapshot.Occurrences.SingleOrDefault(candidate => candidate.Id == requestedChange.OccurrenceId);
            if (item is null || occurrence is null ||
                occurrence.ItemId != item.Id ||
                occurrence.ListId != request.ListId ||
                occurrence.State != OccurrenceState.Open ||
                item.CurrentOccurrenceId != occurrence.Id)
            {
                return InvalidProposal("Each proposed change must target an unfinished occurrence in this list.");
            }

            if (item.Version != requestedChange.ExpectedItemVersion)
            {
                return Conflict<ReviewProposalSnapshot>("An item changed since the review was opened.");
            }

            if (requestedChange.Action == ReviewDisposition.Move)
            {
                if (requestedChange.DestinationListId is not { } destinationId ||
                    requestedChange.ExpectedDestinationListVersion is not { } destinationVersion ||
                    destinationId == request.ListId)
                {
                    return InvalidProposal("A move requires a different destination list and its expected version.");
                }

                var destination = snapshot.Lists.SingleOrDefault(candidate => candidate.Id == destinationId);
                if (destination is null)
                {
                    return DomainResult<ReviewProposalSnapshot>.Failure(
                        new DomainError(DomainErrorCode.NotFound, "The destination list was not found."));
                }

                if (destination.State != JournalListState.Open)
                {
                    return DomainResult<ReviewProposalSnapshot>.Failure(
                        new DomainError(DomainErrorCode.ListClosed, "The destination list is closed."));
                }

                if (destination.Version != destinationVersion)
                {
                    return Conflict<ReviewProposalSnapshot>("The destination list changed since the review was opened.");
                }
            }
            else if (requestedChange.DestinationListId is not null ||
                     requestedChange.ExpectedDestinationListVersion is not null)
            {
                return InvalidProposal("Only move dispositions can have a destination.");
            }

            changes.Add(new ReviewChange(
                requestedChange.ItemId,
                requestedChange.OccurrenceId,
                requestedChange.ExpectedItemVersion,
                requestedChange.Action,
                requestedChange.DestinationListId,
                requestedChange.ExpectedDestinationListVersion));
        }

        if (request.RequestCloseList &&
            !review.Value.Unfinished.Select(entry => entry.Occurrence.Id).ToHashSet().SetEquals(seenOccurrences))
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(new DomainError(
                DomainErrorCode.UnresolvedOccurrences,
                "A closing proposal must explicitly resolve every unfinished occurrence.",
                review.Value.Unfinished
                    .Where(entry => !seenOccurrences.Contains(entry.Occurrence.Id))
                    .Select(entry => entry.Occurrence.Id)
                    .ToArray()));
        }

        var proposalId = Guid.NewGuid();
        for (var index = 0; index < changes.Count; index++)
        {
            changes[index] = changes[index] with { OperationId = DeriveOperationId(proposalId, index) };
        }

        var createdAt = _clock.GetUtcNow().ToUniversalTime();
        var proposal = new CreateReviewProposalCommand(
            proposalId,
            request.ListId,
            request.ExpectedListVersion,
            actor.UserId,
            actor.Source,
            createdAt,
            createdAt + ProposalLifetime,
            request.RequestCloseList,
            changes.AsReadOnly(),
            ComputeDigest(proposalId, request.ListId, request.ExpectedListVersion, request.RequestCloseList, changes));

        return repository.CreateReviewProposal(proposal);
    }

    public DomainResult<ReviewProposalSnapshot> GetProposal(Guid proposalId, ReviewActor actor)
    {
        var actorValidation = ValidateActor(actor);
        if (actorValidation is not null)
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(actorValidation);
        }

        var result = repository.GetReviewProposal(proposalId);
        if (!result.IsSuccess)
        {
            return result;
        }

        return StringComparer.Ordinal.Equals(result.Value!.CreatedBy, actor.UserId)
            ? ExpireIfNecessary(result.Value)
            : Denied<ReviewProposalSnapshot>();
    }

    public DomainResult<ReviewProposalSnapshot> ApproveProposal(
        Guid proposalId,
        string digest,
        ReviewActor actor)
    {
        var actorValidation = ValidateActor(actor);
        if (actorValidation is not null)
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(actorValidation);
        }

        var proposalResult = repository.GetReviewProposal(proposalId);
        if (!proposalResult.IsSuccess)
        {
            return proposalResult;
        }

        var proposal = proposalResult.Value!;
        if (!actor.Interactive || actor.Source != ReviewProposalSource.Pwa ||
            !StringComparer.Ordinal.Equals(proposal.CreatedBy, actor.UserId))
        {
            return Denied<ReviewProposalSnapshot>();
        }

        var expired = ExpireIfNecessary(proposal);
        if (!expired.IsSuccess)
        {
            return expired;
        }
        if (expired.Value!.Status == ReviewProposalStatus.Expired)
        {
            return ProposalExpired<ReviewProposalSnapshot>();
        }

        if (!DigestMatches(proposal.Digest, digest))
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(
                new DomainError(DomainErrorCode.ProposalDigestMismatch, "The proposal digest does not match."));
        }

        return repository.SetReviewProposalStatus(new ReviewProposalStatusCommand(
            proposalId,
            ReviewProposalStatus.Approved,
            actor.UserId,
            digest,
            _clock.GetUtcNow().ToUniversalTime()));
    }

    public DomainResult<ReviewProposalSnapshot> CancelProposal(Guid proposalId, ReviewActor actor) =>
        SetTerminalStatus(proposalId, actor, ReviewProposalStatus.Cancelled);

    public DomainResult<ReviewProposalSnapshot> RejectProposal(Guid proposalId, ReviewActor actor) =>
        SetTerminalStatus(proposalId, actor, ReviewProposalStatus.Rejected);

    public DomainResult<ReviewProposalSnapshot> ApplyProposal(Guid proposalId, ReviewActor actor)
    {
        var actorValidation = ValidateActor(actor);
        if (actorValidation is not null)
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(actorValidation);
        }

        var proposalResult = repository.GetReviewProposal(proposalId);
        if (!proposalResult.IsSuccess)
        {
            return proposalResult;
        }

        var proposal = proposalResult.Value!;
        if (!StringComparer.Ordinal.Equals(proposal.CreatedBy, actor.UserId))
        {
            return Denied<ReviewProposalSnapshot>();
        }

        if (proposal.Status == ReviewProposalStatus.Applied)
        {
            return proposal.ApprovedBy == actor.UserId &&
                   DigestMatches(proposal.Digest, proposal.ApprovedDigest) &&
                   DigestMatches(proposal.Digest, ComputeDigest(
                       proposal.Id,
                       proposal.ListId,
                       proposal.ExpectedListVersion,
                       proposal.RequestCloseList,
                       proposal.Changes))
                ? proposalResult
                : Denied<ReviewProposalSnapshot>();
        }

        var expired = ExpireIfNecessary(proposal);
        if (!expired.IsSuccess)
        {
            return expired;
        }
        if (expired.Value!.Status == ReviewProposalStatus.Expired)
        {
            return ProposalExpired<ReviewProposalSnapshot>();
        }

        if (proposal.Status is ReviewProposalStatus.Cancelled or ReviewProposalStatus.Rejected)
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(
                new DomainError(DomainErrorCode.ProposalTerminal, "The proposal is already terminal."));
        }
        if (proposal.Status == ReviewProposalStatus.Conflicted)
        {
            return Conflict<ReviewProposalSnapshot>("The proposal is stale and requires a new review.");
        }
        if (proposal.Status != ReviewProposalStatus.Approved ||
            proposal.ApprovedBy != actor.UserId ||
            !DigestMatches(proposal.Digest, proposal.ApprovedDigest))
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(
                new DomainError(DomainErrorCode.ApprovalRequired, "The proposal requires a valid approval."));
        }

        var applied = repository.ApplyReviewProposal(new ApplyReviewProposalCommand(
            proposalId,
            actor.UserId,
            proposal.Digest,
            _clock.GetUtcNow().ToUniversalTime()));
        if (applied.IsSuccess && applied.Value!.Status == ReviewProposalStatus.Conflicted)
        {
            var snapshot = repository.GetSnapshot();
            var details = DescribeConflicts(proposal, snapshot);
            return DomainResult<ReviewProposalSnapshot>.Failure(new DomainError(
                DomainErrorCode.ConcurrencyConflict,
                $"The journal changed since this proposal was reviewed. {details} Review the current list and create a new proposal.",
                proposal.Changes.Select(change => change.OccurrenceId).ToArray()));
        }

        return applied;
    }

    private DomainResult<ReviewProposalSnapshot> SetTerminalStatus(
        Guid proposalId,
        ReviewActor actor,
        ReviewProposalStatus status)
    {
        var actorValidation = ValidateActor(actor);
        if (actorValidation is not null)
        {
            return DomainResult<ReviewProposalSnapshot>.Failure(actorValidation);
        }

        var proposal = repository.GetReviewProposal(proposalId);
        if (!proposal.IsSuccess)
        {
            return proposal;
        }

        if (!StringComparer.Ordinal.Equals(proposal.Value!.CreatedBy, actor.UserId))
        {
            return Denied<ReviewProposalSnapshot>();
        }

        var expired = ExpireIfNecessary(proposal.Value);
        if (!expired.IsSuccess)
        {
            return expired;
        }
        if (expired.Value!.Status == ReviewProposalStatus.Expired)
        {
            return ProposalExpired<ReviewProposalSnapshot>();
        }

        return repository.SetReviewProposalStatus(new ReviewProposalStatusCommand(
            proposalId,
            status,
            actor.UserId,
            null,
            _clock.GetUtcNow().ToUniversalTime()));
    }

    private DomainResult<ReviewProposalSnapshot> ExpireIfNecessary(ReviewProposalSnapshot proposal)
    {
        if (proposal.Status is ReviewProposalStatus.Pending or ReviewProposalStatus.Approved &&
            _clock.GetUtcNow() >= proposal.ExpiresAt)
        {
            return repository.SetReviewProposalStatus(new ReviewProposalStatusCommand(
                proposal.Id,
                ReviewProposalStatus.Expired,
                proposal.CreatedBy,
                null,
                _clock.GetUtcNow().ToUniversalTime()));
        }

        return DomainResult<ReviewProposalSnapshot>.Success(proposal);
    }

    private static string DescribeConflicts(ReviewProposalSnapshot proposal, JournalSnapshot snapshot)
    {
        var details = new List<string>();
        var lists = snapshot.Lists.ToDictionary(candidate => candidate.Id);
        var items = snapshot.Items.ToDictionary(candidate => candidate.Id);
        var occurrences = snapshot.Occurrences.ToDictionary(candidate => candidate.Id);
        var list = lists.GetValueOrDefault(proposal.ListId);
        if (list is null)
        {
            details.Add($"List {proposal.ListId} no longer exists.");
        }
        else if (list.Version != proposal.ExpectedListVersion)
        {
            details.Add($"List {list.Id}: expected version {proposal.ExpectedListVersion}, current version {list.Version}.");
        }

        foreach (var change in proposal.Changes)
        {
            var item = items.GetValueOrDefault(change.ItemId);
            if (item is null)
            {
                details.Add($"Item {change.ItemId} no longer exists.");
            }
            else if (item.Version != change.ExpectedItemVersion)
            {
                details.Add($"Item {item.Id}: expected version {change.ExpectedItemVersion}, current version {item.Version}.");
            }

            var occurrence = occurrences.GetValueOrDefault(change.OccurrenceId);
            if (occurrence is null)
            {
                details.Add($"Occurrence {change.OccurrenceId} no longer exists.");
            }
            else if (occurrence.State != OccurrenceState.Open ||
                     item is not null && item.CurrentOccurrenceId != occurrence.Id)
            {
                details.Add($"Occurrence {occurrence.Id} is no longer the current open occurrence (current state: {occurrence.State}).");
            }

            if (change.Action == ReviewDisposition.Move)
            {
                var destination = change.DestinationListId is { } destinationId
                    ? lists.GetValueOrDefault(destinationId)
                    : null;
                if (destination is null)
                {
                    details.Add($"Destination list {change.DestinationListId} no longer exists.");
                }
                else if (destination.State != JournalListState.Open)
                {
                    details.Add($"Destination list {destination.Id} is {destination.State}.");
                }
                else if (destination.Version != change.ExpectedDestinationListVersion)
                {
                    details.Add($"Destination list {destination.Id}: expected version {change.ExpectedDestinationListVersion}, current version {destination.Version}.");
                }
            }
        }

        if (proposal.RequestCloseList && list is not null)
        {
            var changedOccurrences = proposal.Changes.Select(change => change.OccurrenceId).ToHashSet();
            foreach (var occurrence in snapshot.Occurrences.Where(candidate =>
                         candidate.ListId == list.Id &&
                         candidate.State == OccurrenceState.Open &&
                         !changedOccurrences.Contains(candidate.Id)))
            {
                details.Add($"Unresolved occurrence {occurrence.Id} is not in this proposal.");
            }
        }

        return details.Count == 0
            ? "Journal state changed."
            : string.Join(" ", details);
    }

    private static DomainError? ValidateActor(ReviewActor actor) =>
        actor is null || string.IsNullOrWhiteSpace(actor.UserId)
            ? new DomainError(DomainErrorCode.ApprovalDenied, "An authenticated review principal is required.")
            : null;

    private static DomainResult<ReviewProposalSnapshot> InvalidProposal(string message = "The review proposal is invalid.") =>
        DomainResult<ReviewProposalSnapshot>.Failure(new DomainError(DomainErrorCode.InvalidInput, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult<T>.Failure(new DomainError(DomainErrorCode.ConcurrencyConflict, message));

    private static DomainResult<T> Denied<T>() =>
        DomainResult<T>.Failure(new DomainError(DomainErrorCode.ApprovalDenied, "The proposal is not available to this principal."));

    private static DomainResult<T> ProposalExpired<T>() =>
        DomainResult<T>.Failure(new DomainError(DomainErrorCode.ProposalExpired, "The review proposal has expired."));

    private static Guid DeriveOperationId(Guid proposalId, int index)
    {
        Span<byte> input = stackalloc byte[20];
        proposalId.TryWriteBytes(input);
        BitConverter.TryWriteBytes(input[16..], index);
        return new Guid(SHA256.HashData(input)[..16]);
    }

    private static string ComputeDigest(
        Guid proposalId,
        Guid listId,
        long expectedListVersion,
        bool requestCloseList,
        IReadOnlyList<ReviewChange> changes)
    {
        var canonical = new StringBuilder()
            .Append(proposalId.ToString("N"))
            .Append('|').Append(listId.ToString("N"))
            .Append('|').Append(expectedListVersion)
            .Append('|').Append(requestCloseList ? '1' : '0');
        foreach (var change in changes)
        {
            canonical.Append('|').Append(change.ItemId.ToString("N"))
                .Append(':').Append(change.OccurrenceId.ToString("N"))
                .Append(':').Append(change.ExpectedItemVersion)
                .Append(':').Append((int)change.Action)
                .Append(':').Append(change.DestinationListId?.ToString("N") ?? "-")
                .Append(':').Append(change.ExpectedDestinationListVersion?.ToString() ?? "-");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static bool DigestMatches(string expected, string? supplied)
    {
        if (supplied is null || supplied.Length != 64)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(supplied));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
