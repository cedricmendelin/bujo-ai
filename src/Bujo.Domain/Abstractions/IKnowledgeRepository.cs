namespace Bujo.Domain;

public interface IKnowledgeRepository
{
    DomainResult<KnowledgeEntrySnapshot> Create(CreateKnowledgeEntryCommand command);

    DomainResult<KnowledgeEntrySnapshot> Get(Guid id);

    DomainResult<KnowledgeEntrySnapshot> Update(UpdateKnowledgeEntryCommand command);

    DomainResult<KnowledgeEntrySnapshot> SetArchived(
        Guid id,
        bool archived,
        long expectedVersion,
        DateTimeOffset updatedAt);

    DomainResult<KnowledgeEntrySnapshot> Delete(Guid id, long expectedVersion);

    DomainResult<KnowledgeSearchPage> Search(KnowledgeSearchCriteria criteria);

    IReadOnlyList<KnowledgeTagInfo> GetTags();

    KnowledgeIndexState GetIndexState();
}
