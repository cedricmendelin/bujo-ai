using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace Bujo.Integration.Tests;

public sealed class SqlServerKnowledgeTests
{
    [Fact]
    public void KnowledgeMigrationRunsFullTextCatalogDdlOutsideTransactions()
    {
        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer("Server=localhost;Database=BujoMigrationScript;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var context = new JournalDbContext(options);
        var migrator = context.GetService<IMigrator>();

        var up = migrator.GenerateScript(
            "20261119120000_AddKnowledgeEntries",
            "20261119215100_AddKnowledgeFullText");
        AssertOutsideTransaction(up, "CREATE FULLTEXT CATALOG");

        var down = migrator.GenerateScript(
            "20261119215100_AddKnowledgeFullText",
            "20261119120000_AddKnowledgeEntries");
        AssertOutsideTransaction(down, "DROP FULLTEXT CATALOG");

        var rollbackOfPreviouslyAppliedKnowledgeMigration = migrator.GenerateScript(
            "20261119120000_AddKnowledgeEntries",
            "20261118214500_AddReviewProposals");
        AssertOutsideTransaction(rollbackOfPreviouslyAppliedKnowledgeMigration, "DROP FULLTEXT CATALOG");
    }

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

    private static void AssertOutsideTransaction(string script, string statement)
    {
        var statementIndex = script.IndexOf(statement, StringComparison.OrdinalIgnoreCase);
        Assert.True(statementIndex >= 0, $"Expected migration SQL to contain {statement}.");

        var transactionStart = script.LastIndexOf(
            "BEGIN TRANSACTION;",
            statementIndex,
            StringComparison.OrdinalIgnoreCase);
        var transactionCommit = script.LastIndexOf(
            "COMMIT;",
            statementIndex,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            transactionStart < 0 || transactionCommit > transactionStart,
            $"{statement} must run after EF commits its migration transaction.");
    }

    private sealed class KnowledgeContextFactory(DbContextOptions<JournalDbContext> options)
        : IDbContextFactory<JournalDbContext>
    {
        public JournalDbContext CreateDbContext() => new(options);

        public Task<JournalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalDbContext(options));
    }
}
