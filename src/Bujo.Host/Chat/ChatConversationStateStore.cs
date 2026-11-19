using System.Security.Claims;
using Bujo.Domain;

namespace Bujo.Host.Chat;

public enum NextWeekCaptureStatus
{
    AwaitingListChoice,
    AwaitingCreateConfirmation,
    Processing,
    Completed
}

public sealed record NextWeekCaptureState(
    Guid Id,
    string TaskName,
    IsoWeekKey Week,
    IReadOnlyList<Guid> CandidateListIds,
    NextWeekCaptureStatus Status,
    DateTimeOffset ExpiresAt,
    string? Outcome = null);

public sealed class ChatConversationStateStore(TimeProvider? clock = null)
{
    private const int MaximumConversations = 1_024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<ConversationScope, NextWeekCaptureState> _states = [];
    private readonly object _gate = new();

    public bool TryGet(
        ClaimsPrincipal principal,
        string? conversationId,
        out NextWeekCaptureState state)
    {
        state = null!;
        if (!TryGetScope(principal, conversationId, out var scope))
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpired();
            return _states.TryGetValue(scope, out state!);
        }
    }

    public bool Set(
        ClaimsPrincipal principal,
        string? conversationId,
        string taskName,
        IsoWeekKey week,
        IReadOnlyList<Guid> candidateListIds,
        NextWeekCaptureStatus status,
        out NextWeekCaptureState state)
    {
        state = null!;
        if (!TryGetScope(principal, conversationId, out var scope))
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpired();
            if (!_states.ContainsKey(scope) && _states.Count >= MaximumConversations)
            {
                var oldest = _states.MinBy(entry => entry.Value.ExpiresAt);
                _states.Remove(oldest.Key);
            }

            state = new NextWeekCaptureState(
                Guid.NewGuid(),
                taskName,
                week,
                candidateListIds.ToArray(),
                status,
                _clock.GetUtcNow() + Lifetime);
            _states[scope] = state;
            return true;
        }
    }

    public bool TryBegin(
        ClaimsPrincipal principal,
        string? conversationId,
        Guid stateId,
        out NextWeekCaptureState? state)
    {
        state = null!;
        if (!TryGetScope(principal, conversationId, out var scope))
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpired();
            if (!_states.TryGetValue(scope, out var current))
            {
                return false;
            }

            state = current;
            if (current.Id != stateId ||
                current.Status is NextWeekCaptureStatus.Processing or NextWeekCaptureStatus.Completed)
            {
                return false;
            }

            state = current with { Status = NextWeekCaptureStatus.Processing };
            _states[scope] = state;
            return true;
        }
    }

    public void Complete(
        ClaimsPrincipal principal,
        string? conversationId,
        Guid stateId,
        string outcome)
    {
        Update(principal, conversationId, stateId, state => state with
        {
            Status = NextWeekCaptureStatus.Completed,
            Outcome = outcome
        });
    }

    public void Remove(ClaimsPrincipal principal, string? conversationId, Guid stateId)
    {
        if (!TryGetScope(principal, conversationId, out var scope))
        {
            return;
        }

        lock (_gate)
        {
            if (_states.TryGetValue(scope, out var state) && state.Id == stateId &&
                state.Status is NextWeekCaptureStatus.AwaitingListChoice or NextWeekCaptureStatus.AwaitingCreateConfirmation)
            {
                _states.Remove(scope);
            }
        }
    }

    private void Update(
        ClaimsPrincipal principal,
        string? conversationId,
        Guid stateId,
        Func<NextWeekCaptureState, NextWeekCaptureState> update)
    {
        if (!TryGetScope(principal, conversationId, out var scope))
        {
            return;
        }

        lock (_gate)
        {
            if (_states.TryGetValue(scope, out var state) && state.Id == stateId)
            {
                _states[scope] = update(state);
            }
        }
    }

    private void RemoveExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var scope in _states
            .Where(entry => entry.Value.ExpiresAt <= now)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _states.Remove(scope);
        }
    }

    private static bool TryGetScope(
        ClaimsPrincipal principal,
        string? conversationId,
        out ConversationScope scope)
    {
        var tenantId = principal.FindFirst("tid")?.Value;
        var userId = principal.FindFirst("oid")?.Value;
        var normalizedConversationId = conversationId?.Trim();
        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(normalizedConversationId) ||
            tenantId.Length > 128 ||
            userId.Length > 128 ||
            normalizedConversationId.Length > 128)
        {
            scope = default;
            return false;
        }

        scope = new ConversationScope(tenantId, userId, normalizedConversationId);
        return true;
    }

    private readonly record struct ConversationScope(string TenantId, string UserId, string ConversationId);
}
