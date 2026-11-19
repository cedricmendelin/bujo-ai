using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace Bujo.Integration.Tests;

public sealed class SqlServerKnowledgeTests
{
    [Fact]
    public async Task SqlServerMigrationPersistsVerbatimEntriesAndFallbackSearchAcrossRepositoryRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("BUJO_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("Set BUJO_TEST_SQL_CONNECTION to a disposable SQL Server database.");
        }

        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using (var context = new JournalDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        var marker = $"Marker{Guid.NewGuid():N}";
        var content = $"“Exact SQL persistence”\r\n{marker} <i>literal</i>";
        var factory = new KnowledgeContextFactory(options);
        var writer = CreateService(factory);
        var created = writer.Create(
            content,
            "Persistence proof",
            "https://example.invalid/source",
            ["SQL", "Quotes"]);
        Assert.True(created.IsSuccess, created.Error?.Message);

        var restarted = CreateService(factory);
        var read = restarted.Get(created.Value!.Id);
        var search = restarted.Search(new KnowledgeSearchRequest(marker, ["sql", "quotes"]));

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(created.Value.Id, read.Value!.Id);
        Assert.Equal(content, read.Value.Content);
        Assert.Equal("https://example.invalid/source", read.Value.SourceReference);
        Assert.True(search.IsSuccess, search.Error?.Message);
        Assert.Equal(created.Value.Id, Assert.Single(search.Value!.Results).Id);
        Assert.Equal(content, search.Value.Results[0].Passage);
        if (search.Value.IndexState == KnowledgeIndexState.Ready)
        {
            Assert.Equal(KnowledgeSearchMode.FullText, search.Value.SearchMode);
        }
        else
        {
            Assert.Equal(KnowledgeSearchMode.Fallback, search.Value.SearchMode);
        }

        var backup = restarted.ExportBackup();
        var deleted = restarted.Delete(created.Value.Id, read.Value.Version);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        var restored = restarted.RestoreBackup(backup);
        Assert.True(restored.IsSuccess, restored.Error?.Message);
        var afterRestore = CreateService(factory).Get(created.Value.Id);
        Assert.True(afterRestore.IsSuccess, afterRestore.Error?.Message);
        Assert.Equal(content, afterRestore.Value!.Content);
        Assert.Equal("https://example.invalid/source", afterRestore.Value.SourceReference);
    }

    private static KnowledgeApplicationService CreateService(IDbContextFactory<JournalDbContext> factory) =>
        new(new EfKnowledgeRepository(factory, NullLogger<EfKnowledgeRepository>.Instance));

    private sealed class KnowledgeContextFactory(DbContextOptions<JournalDbContext> options)
        : IDbContextFactory<JournalDbContext>
    {
        public JournalDbContext CreateDbContext() => new(options);

        public Task<JournalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalDbContext(options));
    }
}
