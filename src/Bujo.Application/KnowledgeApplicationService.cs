using Bujo.Domain;

namespace Bujo.Application;

public sealed class KnowledgeApplicationService
{
    private readonly IKnowledgeRepository _repository;
    private readonly TimeProvider _clock;

    public KnowledgeApplicationService(IKnowledgeRepository repository, TimeProvider? clock = null)
    {
        _repository = repository;
        _clock = clock ?? TimeProvider.System;
    }

    public DomainResult<KnowledgeEntrySnapshot> Create(
        string content,
        string? title = null,
        string? sourceReference = null,
        IReadOnlyList<string>? tags = null) =>
        _repository.Create(new CreateKnowledgeEntryCommand(
            Guid.NewGuid(),
            content,
            title,
            sourceReference,
            tags,
            _clock.GetUtcNow()));

    public DomainResult<KnowledgeEntrySnapshot> Get(Guid id) => _repository.Get(id);

    public DomainResult<KnowledgeEntrySnapshot> Update(
        Guid id,
        string content,
        string? title,
        string? sourceReference,
        IReadOnlyList<string>? tags,
        long expectedVersion) =>
        _repository.Update(new UpdateKnowledgeEntryCommand(
            id,
            content,
            title,
            sourceReference,
            tags,
            expectedVersion,
            _clock.GetUtcNow()));

    public DomainResult<KnowledgeEntrySnapshot> SetArchived(Guid id, bool archived, long expectedVersion) =>
        _repository.SetArchived(id, archived, expectedVersion, _clock.GetUtcNow());

    public DomainResult<KnowledgeEntrySnapshot> Delete(Guid id, long expectedVersion) =>
        _repository.Delete(id, expectedVersion);

    public DomainResult<KnowledgeSearchResponse> Search(KnowledgeSearchRequest request)
    {
        var criteria = KnowledgeSearchQueryBuilder.Build(request);
        if (!criteria.IsSuccess)
        {
            return DomainResult<KnowledgeSearchResponse>.Failure(criteria.Error!);
        }

        var results = _repository.Search(criteria.Value!);
        return results.IsSuccess
            ? DomainResult<KnowledgeSearchResponse>.Success(new KnowledgeSearchResponse(
                results.Value!.Results,
                KnowledgeSearchQueryBuilder.EncodeCursor(results.Value.NextCursor),
                results.Value.SearchMode,
                results.Value.IndexState))
            : DomainResult<KnowledgeSearchResponse>.Failure(results.Error!);
    }

    public IReadOnlyList<KnowledgeTagInfo> GetTags() => _repository.GetTags();

    public KnowledgeIndexState GetIndexState() => _repository.GetIndexState();
}
