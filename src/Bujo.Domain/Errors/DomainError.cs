namespace Bujo.Domain;

public enum DomainErrorCode
{
    InvalidInput,
    NotFound,
    InvalidTransition,
    ListClosed,
    FutureLogCannotClose,
    UnresolvedOccurrences,
    ConcurrencyConflict,
    StaleOccurrence,
    IdempotencyKeyReuse,
    DuplicateFutureLog,
    DuplicateListName,
    InvalidTimeZone,
    InvariantViolation,
    ApprovalDenied,
    ApprovalRequired,
    ProposalExpired,
    ProposalTerminal,
    ProposalDigestMismatch
}

public sealed record DomainError(
    DomainErrorCode Code,
    string Message,
    IReadOnlyList<Guid>? RelatedIds = null);

public sealed class DomainException : Exception
{
    public DomainException(
        DomainErrorCode code,
        string message,
        IReadOnlyList<Guid>? relatedIds = null)
        : base(message)
    {
        Code = code;
        RelatedIds = relatedIds;
    }

    public DomainErrorCode Code { get; }

    public IReadOnlyList<Guid>? RelatedIds { get; }

    public DomainError ToError() => new(Code, Message, RelatedIds);
}

public sealed class DomainResult<T>
{
    private DomainResult(T? value, DomainError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public DomainError? Error { get; }

    public static DomainResult<T> Success(T value) => new(value, null);

    public static DomainResult<T> Failure(DomainError error) => new(default, error);
}
