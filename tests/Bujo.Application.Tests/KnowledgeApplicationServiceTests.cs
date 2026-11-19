using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Application.Tests;

public sealed class KnowledgeApplicationServiceTests
{
    [Fact]
    public void SearchMatchesPhrasesAndRequiresEveryTag()
    {
        var service = CreateService(KnowledgeIndexState.Ready);
        var first = service.Create(
            "“The quiet design matters”\nA saved quotation.",
            "Design note",
            "https://example.invalid/note",
            ["Design", "Quotes"]);
        var second = service.Create(
            "The quiet design matters, but this is another note.",
            tags: ["Design"]);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);

        var phrase = service.Search(new KnowledgeSearchRequest("\"quiet design\"", ["design", "quotes"]));
        var allTerms = service.Search(new KnowledgeSearchRequest("quiet matters", ["design"]));
        var missingTag = service.Search(new KnowledgeSearchRequest(null, ["design", "missing"]));

        Assert.True(phrase.IsSuccess, phrase.Error?.Message);
        Assert.Single(phrase.Value!.Results);
        Assert.Equal(first.Value!.Id, phrase.Value.Results[0].Id);
        Assert.Equal(first.Value.Content, phrase.Value.Results[0].Passage);
        Assert.Equal(first.Value.SourceReference, phrase.Value.Results[0].SourceReference);
        Assert.Equal(KnowledgeSearchMode.FullText, phrase.Value.SearchMode);
        Assert.True(allTerms.IsSuccess, allTerms.Error?.Message);
        Assert.Equal(2, allTerms.Value!.Results.Count);
        Assert.True(missingTag.IsSuccess, missingTag.Error?.Message);
        Assert.Empty(missingTag.Value!.Results);
    }

    [Fact]
    public void SearchEscapesSpecialInputAndRejectsEmptyOrInvalidQueries()
    {
        var service = CreateService();
        var entry = service.Create("C++ & [quoted] topics remain searchable.");
        Assert.True(entry.IsSuccess, entry.Error?.Message);

        var special = service.Search(new KnowledgeSearchRequest("C++ & [quoted]"));
        var empty = service.Search(new KnowledgeSearchRequest(" \t "));
        var tooLarge = service.Search(new KnowledgeSearchRequest(new string('x', DomainLimits.KnowledgeSearchText + 1)));
        var tooManyTerms = service.Search(new KnowledgeSearchRequest(
            string.Join(' ', Enumerable.Range(0, DomainLimits.KnowledgeSearchTerms + 1).Select(index => $"term{index}"))));

        Assert.True(special.IsSuccess, special.Error?.Message);
        Assert.Single(special.Value!.Results);
        Assert.Equal(DomainErrorCode.InvalidInput, empty.Error!.Code);
        Assert.Equal(DomainErrorCode.InvalidInput, tooLarge.Error!.Code);
        Assert.Equal(DomainErrorCode.InvalidInput, tooManyTerms.Error!.Code);
    }

    [Theory]
    [InlineData(KnowledgeIndexState.Populating)]
    [InlineData(KnowledgeIndexState.Unavailable)]
    public void NewEntriesRemainSearchableAndReportFallbackReadiness(KnowledgeIndexState state)
    {
        var service = CreateService(state);
        var entry = service.Create("Immediately searchable knowledge");

        var result = service.Search(new KnowledgeSearchRequest("Immediately searchable"));

        Assert.True(entry.IsSuccess, entry.Error?.Message);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(KnowledgeSearchMode.Fallback, result.Value!.SearchMode);
        Assert.Equal(state, result.Value.IndexState);
        Assert.Equal(entry.Value!.Id, Assert.Single(result.Value.Results).Id);
    }

    [Fact]
    public void UpdateArchiveAndDeleteUseStableIdsAndExpectedVersions()
    {
        var service = CreateService();
        var created = service.Create("Original content", "Original title", tags: ["Work"]);
        Assert.True(created.IsSuccess, created.Error?.Message);
        var initial = created.Value!;

        var edited = service.Update(
            initial.Id,
            "Edited “verbatim” content",
            "Edited title",
            "source:reference",
            ["Research"],
            initial.Version);
        var stale = service.Update(
            initial.Id,
            "Lost update",
            null,
            null,
            null,
            initial.Version);
        Assert.True(edited.IsSuccess, edited.Error?.Message);
        Assert.Equal(initial.Id, edited.Value!.Id);
        Assert.Equal(initial.CreatedAt, edited.Value.CreatedAt);
        Assert.Equal("Edited “verbatim” content", edited.Value.Content);
        Assert.Equal(initial.Version + 1, edited.Value.Version);
        Assert.Equal(DomainErrorCode.ConcurrencyConflict, stale.Error!.Code);

        var archived = service.SetArchived(initial.Id, true, edited.Value.Version);
        var hidden = service.Search(new KnowledgeSearchRequest("verbatim"));
        var visible = service.Search(new KnowledgeSearchRequest("verbatim", IncludeArchived: true));
        var readArchived = service.Get(initial.Id);
        Assert.True(archived.IsSuccess, archived.Error?.Message);
        Assert.Empty(hidden.Value!.Results);
        Assert.Single(visible.Value!.Results);
        Assert.Equal(archived.Value!.ArchivedAt, readArchived.Value!.ArchivedAt);

        var restored = service.SetArchived(initial.Id, false, archived.Value.Version);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.Null(restored.Value!.ArchivedAt);
        var deleted = service.Delete(initial.Id, restored.Value.Version);
        var missing = service.Get(initial.Id);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Equal(DomainErrorCode.NotFound, missing.Error!.Code);
    }

    [Fact]
    public void SearchUsesBoundedKeysetPaginationAndBindsCursorToTheQuery()
    {
        var service = CreateService(KnowledgeIndexState.Ready);
        for (var index = 0; index < 5; index++)
        {
            Assert.True(service.Create($"shared phrase entry {index}", tags: ["Page"]).IsSuccess);
        }

        var first = service.Search(new KnowledgeSearchRequest("shared phrase", ["Page"], PageSize: 2));
        var second = service.Search(new KnowledgeSearchRequest(
            "shared phrase",
            ["Page"],
            PageSize: 2,
            Cursor: first.Value!.NextCursor));
        var third = service.Search(new KnowledgeSearchRequest(
            "shared phrase",
            ["Page"],
            PageSize: 2,
            Cursor: second.Value!.NextCursor));
        var mismatch = service.Search(new KnowledgeSearchRequest(
            "different",
            ["Page"],
            PageSize: 2,
            Cursor: first.Value.NextCursor));

        Assert.Equal(2, first.Value.Results.Count);
        Assert.Equal(2, second.Value!.Results.Count);
        Assert.Single(third.Value!.Results);
        Assert.Equal(5, first.Value.Results.Concat(second.Value.Results).Concat(third.Value.Results)
            .Select(result => result.Id).Distinct().Count());
        Assert.Equal(DomainErrorCode.InvalidInput, mismatch.Error!.Code);
    }

    [Fact]
    public void BackupRestorePreservesEntryIdentityTextMetadataAndArchiveState()
    {
        var service = CreateService();
        const string content = "“Exact wording”\r\nUnicode: café; markup: <b>literal</b>";
        var created = service.Create(content, "Saved quote", "https://example.invalid/source", ["Quotes", "Research"]);
        Assert.True(created.IsSuccess, created.Error?.Message);
        var archived = service.SetArchived(created.Value!.Id, true, created.Value.Version);
        Assert.True(archived.IsSuccess, archived.Error?.Message);
        var backup = service.ExportBackup();

        Assert.True(service.Create("Temporary entry").IsSuccess);
        var restored = service.RestoreBackup(backup);
        var read = service.Get(created.Value.Id);

        Assert.True(restored.IsSuccess, restored.Error?.Message);
        var entry = Assert.Single(restored.Value!.Entries);
        Assert.Equal(created.Value.Id, entry.Id);
        Assert.Equal(content, entry.Content);
        Assert.Equal("https://example.invalid/source", entry.SourceReference);
        Assert.Equal(new[] { "Quotes", "Research" }, entry.Tags);
        Assert.Equal(archived.Value!.ArchivedAt, entry.ArchivedAt);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(content, read.Value!.Content);
    }

    [Fact]
    public void InvalidBackupDoesNotReplaceExistingEntries()
    {
        var service = CreateService();
        var created = service.Create("Existing entry");
        Assert.True(created.IsSuccess, created.Error?.Message);
        var snapshot = service.ExportBackup();
        var duplicate = snapshot.Entries[0] with { Content = "Duplicate ID" };

        var restored = service.RestoreBackup(snapshot with
        {
            Entries = [snapshot.Entries[0], duplicate]
        });
        var read = service.Get(created.Value!.Id);

        Assert.False(restored.IsSuccess);
        Assert.Equal(DomainErrorCode.InvalidInput, restored.Error!.Code);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal("Existing entry", read.Value!.Content);
    }

    private static KnowledgeApplicationService CreateService(KnowledgeIndexState indexState = KnowledgeIndexState.Unavailable) =>
        new(new InMemoryKnowledgeRepository(indexState), new FixedTimeProvider());

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    }
}
