using System.Data.Common;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bujo.Integration.Tests;

public sealed class JournalSqlPersistenceTests
{
    [Fact]
    public async Task EmptyDatabaseMigratesAndJournalStateSurvivesRepositoryRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("BUJO_SQL_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "BUJO_SQL_TEST_CONNECTION must point to a SQL Server instance with database-creation permissions.");

        var databaseName = $"BujoJournalTests_{Guid.NewGuid():N}";
        var adminOptions = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var adminContext = new JournalDbContext(adminOptions);
        var quotedDatabaseName = $"[{databaseName.Replace("]", "]]", StringComparison.Ordinal)}]";
        await using var adminConnection = adminContext.Database.GetDbConnection();
        await adminConnection.OpenAsync();
        await using (var createDatabase = adminConnection.CreateCommand())
        {
            createDatabase.CommandText = $"CREATE DATABASE {quotedDatabaseName}";
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var journalConnectionBuilder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            journalConnectionBuilder["Initial Catalog"] = databaseName;
            var options = new DbContextOptionsBuilder<JournalDbContext>()
                .UseSqlServer(journalConnectionBuilder.ConnectionString)
                .Options;
            var contextFactory = new TestContextFactory(options);

            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
                await context.Database.MigrateAsync();
                Assert.Single(await context.Database.GetAppliedMigrationsAsync());
            }

            var journal = new JournalService(new EfJournalRepository(contextFactory));
            var captured = journal.Capture("Persisted item");
            Assert.True(captured.IsSuccess);

            var weeklyList = journal.CreateList(
                "Week 48",
                JournalListKind.Weekly,
                weekKey: new IsoWeekKey(2026, 48));
            Assert.True(weeklyList.IsSuccess);

            var moved = journal.Move(
                Guid.NewGuid(),
                captured.Value!.Item.Id,
                captured.Value.Occurrence.Id,
                weeklyList.Value!.Id);
            Assert.True(moved.IsSuccess);

            var completed = journal.Transition(moved.Value!.Resolution.CurrentOccurrence.Id, OccurrenceAction.Done);
            Assert.True(completed.IsSuccess);

            var restartedJournal = new JournalService(new EfJournalRepository(contextFactory));
            var resolved = restartedJournal.Resolve(captured.Value.Occurrence.Id);

            Assert.True(resolved.IsSuccess);
            Assert.Equal(OccurrenceState.Done, resolved.Value!.CurrentOccurrence.State);
            Assert.Equal(weeklyList.Value.Id, resolved.Value.CurrentList.Id);
        }
        finally
        {
            await using var dropDatabase = adminConnection.CreateCommand();
            dropDatabase.CommandText =
                $"ALTER DATABASE {quotedDatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quotedDatabaseName}";
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestContextFactory(DbContextOptions<JournalDbContext> options)
        : IDbContextFactory<JournalDbContext>
    {
        public JournalDbContext CreateDbContext() => new(options);

        public Task<JournalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
