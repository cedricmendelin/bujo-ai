using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bujo.Host;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Encodings.Web;

namespace Bujo.Integration.Tests;

public sealed class JournalSqlPersistenceTests
{
    [Fact]
    public void Ef_model_uses_normalized_journal_tables_and_integrity_indexes()
    {
        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer("Server=localhost;Database=bujo-model-test;Integrated Security=true;TrustServerCertificate=True")
            .Options;
        using var context = new JournalDbContext(options);
        var entityTypes = context.Model.GetEntityTypes().ToArray();

        Assert.DoesNotContain(entityTypes, entity => entity.GetTableName() == "JournalDocuments");
        Assert.Contains(entityTypes, entity => entity.GetTableName() == "Items");
        Assert.Contains(entityTypes, entity => entity.GetTableName() == "JournalLists");
        Assert.Contains(entityTypes, entity => entity.GetTableName() == "ItemTags");
        Assert.Contains(entityTypes, entity => entity.GetTableName() == "Occurrences");
        Assert.Contains(entityTypes, entity => entity.GetTableName() == "JournalEvents");

        var occurrence = context.Model.FindEntityType(typeof(OccurrenceEntity));
        Assert.NotNull(occurrence);
        Assert.Contains(occurrence.GetIndexes(), index => index.IsUnique && index.GetFilter() == "[State] = 0");
        Assert.Contains(
            occurrence.GetIndexes(),
            index => index.IsUnique && index.GetFilter() == "[PreviousOccurrenceId] IS NOT NULL");

        var journalList = context.Model.FindEntityType(typeof(JournalListEntity));
        Assert.NotNull(journalList);
        Assert.Contains(journalList.GetIndexes(), index => index.IsUnique && index.GetFilter() == "[Kind] = 0");
    }

    [Fact]
    public async Task EmptyDatabaseMigratesAndJournalStateSurvivesMultiHopRestart()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
                await context.Database.MigrateAsync();
                Assert.Single(await context.Database.GetAppliedMigrationsAsync());
            }

            var journal = new JournalService(new EfJournalRepository(contextFactory));
            var captured = journal.Capture(
                "Persisted item",
                tags: ["Work"],
                plannedPeriod: PlannedPeriod.ForWeek(new IsoWeekKey(2026, 48)));
            Assert.True(captured.IsSuccess, captured.Error?.Message);

            var week48 = journal.CreateList("Week 48", JournalListKind.Weekly, weekKey: new IsoWeekKey(2026, 48));
            var week49 = journal.CreateList("Week 49", JournalListKind.Weekly, weekKey: new IsoWeekKey(2026, 49));
            var week50 = journal.CreateList("Week 50", JournalListKind.Weekly, weekKey: new IsoWeekKey(2026, 50));
            Assert.True(week48.IsSuccess, week48.Error?.Message);
            Assert.True(week49.IsSuccess, week49.Error?.Message);
            Assert.True(week50.IsSuccess, week50.Error?.Message);

            var firstOperationId = Guid.NewGuid();
            var firstMove = journal.Move(
                firstOperationId,
                captured.Value!.Item.Id,
                captured.Value.Occurrence.Id,
                week48.Value!.Id);
            Assert.True(firstMove.IsSuccess, firstMove.Error?.Message);

            var secondMove = journal.Move(
                Guid.NewGuid(),
                captured.Value.Item.Id,
                firstMove.Value!.Resolution.CurrentOccurrence.Id,
                week49.Value!.Id);
            Assert.True(secondMove.IsSuccess, secondMove.Error?.Message);

            var completed = journal.Transition(secondMove.Value!.Resolution.CurrentOccurrence.Id, OccurrenceAction.Done);
            Assert.True(completed.IsSuccess, completed.Error?.Message);

            var restartedJournal = new JournalService(new EfJournalRepository(contextFactory));
            var resolved = restartedJournal.Resolve(captured.Value.Occurrence.Id);

            Assert.True(resolved.IsSuccess);
            Assert.Equal(OccurrenceState.Done, resolved.Value!.CurrentOccurrence.State);
            Assert.Equal(week49.Value.Id, resolved.Value.CurrentList.Id);
            Assert.Equal(3, resolved.Value.Chain.Count);
            Assert.Equal(["Work"], resolved.Value.Item.Tags.Select(tag => tag.Value));
            Assert.Equal(PlannedPeriod.ForWeek(new IsoWeekKey(2026, 48)), resolved.Value.Item.PlannedPeriod);

            var historyBeforeRetry = restartedJournal.GetSnapshot().History;
            var retry = restartedJournal.Move(
                firstOperationId,
                captured.Value.Item.Id,
                captured.Value.Occurrence.Id,
                week48.Value.Id);
            var historyAfterRetry = restartedJournal.GetSnapshot().History;
            Assert.True(retry.IsSuccess);
            Assert.Equal(firstMove.Value.HistoryEntry, retry.Value!.HistoryEntry);
            Assert.Equal(firstMove.Value.Resolution.CurrentOccurrence.Id, retry.Value.Resolution.CurrentOccurrence.Id);
            Assert.Equal(historyBeforeRetry, historyAfterRetry);

            var reusedOperation = restartedJournal.Move(
                firstOperationId,
                captured.Value.Item.Id,
                captured.Value.Occurrence.Id,
                week50.Value!.Id);
            Assert.False(reusedOperation.IsSuccess);
            Assert.Equal(DomainErrorCode.IdempotencyKeyReuse, reusedOperation.Error!.Code);

            await using var verifyContext = await contextFactory.CreateDbContextAsync();
            Assert.Equal(1, await verifyContext.Items.CountAsync());
            Assert.Equal(4, await verifyContext.Lists.CountAsync());
            Assert.Equal(3, await verifyContext.Occurrences.CountAsync());
            Assert.Equal(1, await verifyContext.ItemTags.CountAsync());
            Assert.Equal(4, await verifyContext.JournalEvents.CountAsync());
            Assert.Equal(2, await verifyContext.MoveOperations.CountAsync());
        });
    }

    [Fact]
    public async Task ApiCaptureMultiHopMoveCompletionAndResolutionSurviveHostRestart()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            Guid itemId;
            Guid sourceOccurrenceId;
            Guid finalListId;
            using (var factory = new JournalSqlApiFactory(contextFactory.ConnectionString))
            using (var client = factory.CreateClient())
            {
                using var captured = await SendJsonAsync(client, HttpMethod.Post, "/api/items", new
                {
                    name = "API persisted item",
                    tags = new[] { "Work" },
                    plannedPeriod = new { kind = "week", weekYear = 2026, week = 48 }
                });
                var captureRoot = captured.RootElement;
                itemId = captureRoot.GetProperty("item").GetProperty("id").GetGuid();
                sourceOccurrenceId = captureRoot.GetProperty("occurrence").GetProperty("id").GetGuid();

                using var week48 = await SendJsonAsync(client, HttpMethod.Post, "/api/lists", new
                {
                    name = "Week 48",
                    kind = JournalListKind.Weekly,
                    weekKey = new { year = 2026, week = 48 }
                });
                using var week49 = await SendJsonAsync(client, HttpMethod.Post, "/api/lists", new
                {
                    name = "Week 49",
                    kind = JournalListKind.Weekly,
                    weekKey = new { year = 2026, week = 49 }
                });
                var week48Id = week48.RootElement.GetProperty("id").GetGuid();
                finalListId = week49.RootElement.GetProperty("id").GetGuid();

                using var firstMove = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/items/{itemId}/move",
                    new
                    {
                        operationId = Guid.NewGuid(),
                        expectedOccurrenceId = sourceOccurrenceId,
                        destinationListId = week48Id
                    });
                var intermediateOccurrenceId = firstMove.RootElement
                    .GetProperty("resolution")
                    .GetProperty("currentOccurrence")
                    .GetProperty("id")
                    .GetGuid();
                using var secondMove = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/items/{itemId}/move",
                    new
                    {
                        operationId = Guid.NewGuid(),
                        expectedOccurrenceId = intermediateOccurrenceId,
                        destinationListId = finalListId
                    });
                var finalOccurrenceId = secondMove.RootElement
                    .GetProperty("resolution")
                    .GetProperty("currentOccurrence")
                    .GetProperty("id")
                    .GetGuid();

                using var completed = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/occurrences/{finalOccurrenceId}/transition",
                    new { action = OccurrenceAction.Done });
                Assert.Equal(
                    OccurrenceState.Done,
                    ReadEnum<OccurrenceState>(completed.RootElement.GetProperty("state")));
            }

            using (var restartedFactory = new JournalSqlApiFactory(contextFactory.ConnectionString))
            using (var restartedClient = restartedFactory.CreateClient())
            using (var resolution = await SendJsonAsync(
                restartedClient,
                HttpMethod.Get,
                $"/api/occurrences/{sourceOccurrenceId}/resolution"))
            {
                var result = resolution.RootElement;
                var item = result.GetProperty("item");
                Assert.Equal(itemId, item.GetProperty("id").GetGuid());
                Assert.Equal(PlannedPeriodKind.Week, ReadEnum<PlannedPeriodKind>(
                    item.GetProperty("plannedPeriod").GetProperty("kind")));
                Assert.Contains(
                    item.GetProperty("tags").EnumerateArray(),
                    tag => tag.GetProperty("value").GetString() == "Work");
                Assert.Equal(finalListId, result.GetProperty("currentList").GetProperty("id").GetGuid());
                Assert.Equal(49, result.GetProperty("currentList").GetProperty("weekKey").GetProperty("week").GetInt32());
                Assert.Equal(
                    OccurrenceState.Done,
                    ReadEnum<OccurrenceState>(result.GetProperty("currentOccurrence").GetProperty("state")));
                Assert.Equal(3, result.GetProperty("chain").GetArrayLength());
            }
        });
    }

    [Fact]
    public async Task Failed_move_event_insert_rolls_back_the_whole_transaction()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new EfJournalRepository(contextFactory);
            var journal = new JournalService(repository);
            var captured = journal.Capture("Rollback item");
            var destination = journal.CreateList("Rollback destination", JournalListKind.Weekly);
            Assert.True(captured.IsSuccess);
            Assert.True(destination.IsSuccess);
            var before = journal.GetSnapshot();

            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                await context.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TRIGGER FailMovedEvent ON dbo.JournalEvents
                    AFTER INSERT
                    AS
                    BEGIN
                        IF EXISTS (SELECT 1 FROM inserted WHERE Kind = 1)
                        BEGIN
                            THROW 51000, 'Injected move failure.', 1;
                        END
                    END
                    """);
            }

            var source = before.Occurrences.Single(item => item.Id == captured.Value!.Occurrence.Id);
            var item = before.Items.Single(candidate => candidate.Id == source.ItemId);
            var target = before.Lists.Single(candidate => candidate.Id == destination.Value!.Id);
            var command = new MoveCommand(
                Guid.NewGuid(),
                item.Id,
                source.Id,
                target.Id,
                item.Version,
                target.Version,
                DateTimeOffset.UtcNow);

            Assert.Throws<DbUpdateException>(() => repository.Move(command));

            var after = repository.GetSnapshot();
            Assert.Equal(before.Items, after.Items);
            Assert.Equal(before.Lists, after.Lists);
            Assert.Equal(before.Occurrences, after.Occurrences);
            Assert.Equal(before.History, after.History);
            await using var verifyContext = await contextFactory.CreateDbContextAsync();
            Assert.Empty(await verifyContext.MoveOperations.ToArrayAsync());
        });
    }

    [Fact]
    public async Task Concurrent_moves_have_one_open_destination()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new EfJournalRepository(contextFactory);
            var journal = new JournalService(repository);
            var captured = journal.Capture("Concurrent item");
            var firstDestination = journal.CreateList("First destination", JournalListKind.Weekly);
            var secondDestination = journal.CreateList("Second destination", JournalListKind.Weekly);
            Assert.True(captured.IsSuccess);
            Assert.True(firstDestination.IsSuccess);
            Assert.True(secondDestination.IsSuccess);

            var snapshot = journal.GetSnapshot();
            var item = snapshot.Items.Single(candidate => candidate.Id == captured.Value!.Item.Id);
            var source = snapshot.Occurrences.Single(candidate => candidate.Id == item.CurrentOccurrenceId);
            var destinations = new[]
            {
                snapshot.Lists.Single(candidate => candidate.Id == firstDestination.Value!.Id),
                snapshot.Lists.Single(candidate => candidate.Id == secondDestination.Value!.Id)
            };
            using var start = new ManualResetEventSlim(false);
            var moves = destinations.Select(destination => new MoveCommand(
                Guid.NewGuid(),
                item.Id,
                source.Id,
                destination.Id,
                item.Version,
                destination.Version,
                DateTimeOffset.UtcNow)).ToArray();
            var tasks = moves.Select(command => Task.Run(() =>
            {
                start.Wait();
                return repository.Move(command);
            })).ToArray();

            start.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, result => result.IsSuccess);
            Assert.All(
                results.Where(result => !result.IsSuccess),
                result => Assert.Contains(
                    result.Error!.Code,
                    new[] { DomainErrorCode.StaleOccurrence, DomainErrorCode.ConcurrencyConflict }));
            var after = repository.GetSnapshot();
            Assert.Single(after.Occurrences, occurrence =>
                occurrence.ItemId == item.Id && occurrence.State == OccurrenceState.Open);
            Assert.Single(after.MoveHistory);
        });
    }

    [Fact]
    public async Task Concurrent_move_and_close_never_leave_an_open_occurrence_on_a_closed_list()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new EfJournalRepository(contextFactory);
            var journal = new JournalService(repository);
            var captured = journal.Capture("Close race item");
            var destination = journal.CreateList("Close race destination", JournalListKind.Weekly);
            Assert.True(captured.IsSuccess);
            Assert.True(destination.IsSuccess);

            var snapshot = journal.GetSnapshot();
            var item = snapshot.Items.Single(candidate => candidate.Id == captured.Value!.Item.Id);
            var source = snapshot.Occurrences.Single(candidate => candidate.Id == item.CurrentOccurrenceId);
            var target = snapshot.Lists.Single(candidate => candidate.Id == destination.Value!.Id);
            var move = new MoveCommand(
                Guid.NewGuid(),
                item.Id,
                source.Id,
                target.Id,
                item.Version,
                target.Version,
                DateTimeOffset.UtcNow);
            var close = new ListVersionCommand(target.Id, target.Version, DateTimeOffset.UtcNow);
            using var start = new ManualResetEventSlim(false);
            var moveTask = Task.Run(() =>
            {
                start.Wait();
                return repository.Move(move);
            });
            var closeTask = Task.Run(() =>
            {
                start.Wait();
                return repository.CloseList(close);
            });

            start.Set();
            var moveResult = await moveTask;
            var closeResult = await closeTask;

            var after = repository.GetSnapshot();
            var list = after.Lists.Single(candidate => candidate.Id == target.Id);
            var openOccurrences = after.Occurrences.Where(occurrence =>
                occurrence.ListId == target.Id && occurrence.State == OccurrenceState.Open);
            if (moveResult.IsSuccess)
            {
                Assert.False(closeResult.IsSuccess);
                Assert.Contains(
                    closeResult.Error!.Code,
                    new[] { DomainErrorCode.UnresolvedOccurrences, DomainErrorCode.ConcurrencyConflict });
                Assert.Equal(JournalListState.Open, list.State);
                Assert.Single(openOccurrences);
            }
            else
            {
                Assert.True(closeResult.IsSuccess, closeResult.Error?.Message);
                Assert.Contains(
                    moveResult.Error!.Code,
                    new[] { DomainErrorCode.ListClosed, DomainErrorCode.ConcurrencyConflict });
                Assert.Equal(JournalListState.Closed, list.State);
                Assert.Empty(openOccurrences);
            }
        });
    }

    [Fact]
    public async Task Concurrent_retries_with_the_same_move_id_return_the_original_result()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                await context.Database.MigrateAsync();
            }

            var repository = new EfJournalRepository(contextFactory);
            var journal = new JournalService(repository);
            var captured = journal.Capture("Idempotent concurrent item");
            var destination = journal.CreateList("Idempotent destination", JournalListKind.Weekly);
            Assert.True(captured.IsSuccess);
            Assert.True(destination.IsSuccess);

            var snapshot = journal.GetSnapshot();
            var item = snapshot.Items.Single(candidate => candidate.Id == captured.Value!.Item.Id);
            var source = snapshot.Occurrences.Single(candidate => candidate.Id == item.CurrentOccurrenceId);
            var target = snapshot.Lists.Single(candidate => candidate.Id == destination.Value!.Id);
            var command = new MoveCommand(
                Guid.NewGuid(),
                item.Id,
                source.Id,
                target.Id,
                item.Version,
                target.Version,
                DateTimeOffset.UtcNow);
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return repository.Move(command);
            })).ToArray();

            start.Set();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
            Assert.Equal(results[0].Value!.HistoryEntry, results[1].Value!.HistoryEntry);
            await using var verifyContext = await contextFactory.CreateDbContextAsync();
            Assert.Single(await verifyContext.MoveOperations.ToArrayAsync());
            Assert.Single(repository.GetSnapshot().MoveHistory);
        });
    }

    [Fact]
    public async Task ReadinessRejectsUnrelatedMigrationHistory()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await context.Database.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE [__EFMigrationsHistory] SET [MigrationId] = N'00000000000000_Unrelated'");

            var check = new JournalDatabaseHealthCheck(
                contextFactory, NullLogger<JournalDatabaseHealthCheck>.Instance);
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal("Required journal migrations are missing.", result.Description);
        });
    }

    [Fact]
    public async Task ReadinessVerifiesRuntimeDataPermissionsWithoutDdlOrWrites()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using var adminContext = await contextFactory.CreateDbContextAsync();
            await adminContext.Database.MigrateAsync();
            await adminContext.Database.OpenConnectionAsync();
            await adminContext.Database.ExecuteSqlRawAsync("""
                CREATE USER [ReadinessRuntime] WITHOUT LOGIN;
                ALTER ROLE db_datareader ADD MEMBER [ReadinessRuntime];
                ALTER ROLE db_datawriter ADD MEMBER [ReadinessRuntime];
                EXECUTE AS USER = N'ReadinessRuntime';
                """);
            try
            {
                var options = new DbContextOptionsBuilder<JournalDbContext>()
                    .UseSqlServer(adminContext.Database.GetDbConnection())
                    .Options;
                var runtimeFactory = new TestContextFactory(options, contextFactory.ConnectionString);
                var check = new JournalDatabaseHealthCheck(
                    runtimeFactory, NullLogger<JournalDatabaseHealthCheck>.Instance);

                var healthy = await check.CheckHealthAsync(new HealthCheckContext());
                Assert.Equal(HealthStatus.Healthy, healthy.Status);
                Assert.Equal(0, await adminContext.Database.SqlQuery<int>(
                    $"SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') AS [Value]").SingleAsync());
                Assert.Empty(await adminContext.Items.ToArrayAsync());

                await adminContext.Database.ExecuteSqlRawAsync("""
                    REVERT;
                    DENY UPDATE ON OBJECT::dbo.Items TO [ReadinessRuntime];
                    EXECUTE AS USER = N'ReadinessRuntime';
                    """);
                var denied = await check.CheckHealthAsync(new HealthCheckContext());

                Assert.Equal(HealthStatus.Unhealthy, denied.Status);
                Assert.Equal("Required journal data permissions are missing.", denied.Description);
                Assert.Empty(await adminContext.Items.ToArrayAsync());
            }
            finally
            {
                await adminContext.Database.ExecuteSqlRawAsync("REVERT;");
            }
        });
    }

    [Fact]
    public async Task StartupVerificationRejectsAnUnmigratedDatabase()
    {
        await WithFreshDatabaseAsync(contextFactory =>
        {
            using var factory = new JournalSqlApiFactory(contextFactory.ConnectionString, verifyOnStart: true);

            var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

            Assert.Contains("Journal database verification failed.", exception.Message);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StartupVerificationPreservesJournalAuthorization()
    {
        await WithFreshDatabaseAsync(async contextFactory =>
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await context.Database.MigrateAsync();
            using var factory = new JournalSqlApiFactory(contextFactory.ConnectionString, verifyOnStart: true);
            using var client = factory.CreateClient();

            using var anonymous = await client.GetAsync("/api/journal");
            using var live = await client.GetAsync("/health/live");
            using var authenticated = await SendJsonAsync(client, HttpMethod.Get, "/api/journal");

            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        });
    }

    private static async Task WithFreshDatabaseAsync(Func<TestContextFactory, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("BUJO_SQL_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "BUJO_SQL_TEST_CONNECTION must point to a SQL Server instance with database-creation permissions.");

        var databaseName = $"BujoJournalTests_{Guid.NewGuid():N}";
        var quotedDatabaseName = $"[{databaseName.Replace("]", "]]", StringComparison.Ordinal)}]";
        var adminOptions = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var adminContext = new JournalDbContext(adminOptions);
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
                .UseSqlServer(
                    journalConnectionBuilder.ConnectionString,
                    sqlServer => sqlServer.EnableRetryOnFailure())
                .Options;
            await test(new TestContextFactory(options, journalConnectionBuilder.ConnectionString));
        }
        finally
        {
            await using var dropDatabase = adminConnection.CreateCommand();
            dropDatabase.CommandText =
                $"ALTER DATABASE {quotedDatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quotedDatabaseName}";
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestContextFactory(
        DbContextOptions<JournalDbContext> options,
        string connectionString)
        : IDbContextFactory<JournalDbContext>
    {
        public string ConnectionString { get; } = connectionString;

        public JournalDbContext CreateDbContext() => new(options);

        public Task<JournalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static async Task<JsonDocument> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string uri,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("Authorization", "Test allow");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected HTTP 200 but received {(int)response.StatusCode}: {responseBody}");
        return JsonDocument.Parse(responseBody);
    }

    private static TEnum ReadEnum<TEnum>(JsonElement value)
        where TEnum : struct, Enum =>
        value.ValueKind == JsonValueKind.String
            ? Enum.Parse<TEnum>(value.GetString()!)
            : (TEnum)Enum.ToObject(typeof(TEnum), value.GetInt32());
}

internal sealed class JournalSqlApiFactory(string connectionString, bool verifyOnStart = false)
    : WebApplicationFactory<JournalDatabaseHealthCheck>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:journal", connectionString);
        builder.UseSetting("Deployment:VerifyDatabaseOnStart", verifyOnStart.ToString());
        builder.UseSetting("Auth:TenantId", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7");
        builder.UseSetting("Auth:Audience", "journal-api");
        builder.UseSetting(
            "Auth:AllowedPrincipals",
            "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7:2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
                options.Schemes.Single(scheme => scheme.Name == "Bearer").HandlerType = typeof(TestAuthenticationHandler));
        });
    }
}
