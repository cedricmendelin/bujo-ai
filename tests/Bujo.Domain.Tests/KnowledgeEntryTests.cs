using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class KnowledgeEntryTests
{
    [Fact]
    public void EntryPreservesOriginalContentAndOptionalMetadata()
    {
        const string content = "“Exact wording”\r\nCafé <b>markup</b> #literal";
        var createdAt = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        var entry = KnowledgeEntry.Create(
            Guid.NewGuid(),
            content,
            null,
            null,
            [" Work ", "work", "Quotes"],
            createdAt);

        var snapshot = entry.ToSnapshot();

        Assert.Equal(content, snapshot.Content);
        Assert.Null(snapshot.Title);
        Assert.Null(snapshot.SourceReference);
        Assert.Equal(["Work", "Quotes"], snapshot.Tags.Select(tag => tag.Value));
        Assert.Equal(createdAt, snapshot.CreatedAt);
        Assert.Equal(createdAt, snapshot.UpdatedAt);
        Assert.Null(snapshot.ArchivedAt);
        Assert.Equal(1, snapshot.Version);
    }

    [Fact]
    public void EditPreservesStableIdAndCreationTimeAndArchiveIsReversible()
    {
        var createdAt = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        var entry = KnowledgeEntry.Create(
            Guid.NewGuid(),
            "Original quote",
            "Title",
            "https://example.invalid/source",
            ["Quote"],
            createdAt);

        var edited = entry.Edit(
            "Revised quote",
            "Revised title",
            "source:reference",
            ["Research"],
            createdAt);
        var archived = entry.SetArchived(true, createdAt);
        var restored = entry.SetArchived(false, createdAt);

        Assert.Equal(entry.Id, edited.Id);
        Assert.Equal(createdAt, edited.CreatedAt);
        Assert.Equal("Revised quote", edited.Content);
        Assert.Equal(createdAt.AddTicks(1), edited.UpdatedAt);
        Assert.Equal(createdAt.AddTicks(2), archived.UpdatedAt);
        Assert.Equal(archived.UpdatedAt, archived.ArchivedAt);
        Assert.Null(restored.ArchivedAt);
        Assert.Equal(4, restored.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void EntryRejectsEmptyContentWithoutNormalizingNonemptyContent(string content)
    {
        var error = Assert.Throws<DomainException>(() =>
            KnowledgeEntry.Create(Guid.NewGuid(), content, null, null, null, DateTimeOffset.UtcNow));

        Assert.Equal(DomainErrorCode.InvalidInput, error.Code);
    }

    [Fact]
    public void EntryEnforcesContentAndTagBounds()
    {
        var tooLong = new string('x', DomainLimits.KnowledgeContent + 1);
        var contentError = Assert.Throws<DomainException>(() =>
            KnowledgeEntry.Create(Guid.NewGuid(), tooLong, null, null, null, DateTimeOffset.UtcNow));
        var tagError = Assert.Throws<DomainException>(() =>
            KnowledgeEntry.Create(
                Guid.NewGuid(),
                "Content",
                null,
                null,
                Enumerable.Range(0, DomainLimits.KnowledgeTagsPerEntry + 1).Select(index => $"tag-{index}"),
                DateTimeOffset.UtcNow));

        Assert.Equal(DomainErrorCode.InvalidInput, contentError.Code);
        Assert.Equal(DomainErrorCode.InvalidInput, tagError.Code);
    }
}
