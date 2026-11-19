using System.Data.Common;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bujo.Host;

public sealed class JournalDatabaseHealthCheck(
    IDbContextFactory<JournalDbContext> contextFactory,
    IKnowledgeRepository knowledgeRepository,
    ILogger<JournalDatabaseHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var expectedMigrations = db.Database.GetMigrations().ToArray();
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
            if (expectedMigrations.Length == 0 || expectedMigrations.Except(appliedMigrations).Any())
            {
                logger.LogError("Journal database is missing migrations required by this release.");
                return HealthCheckResult.Unhealthy("Required journal migrations are missing.");
            }

            var tables = db.Model.GetEntityTypes()
                .Select(entity => $"{entity.GetSchema() ?? "dbo"}.{entity.GetTableName()}")
                .Distinct(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                var allowed = await db.Database.SqlQuery<int>($"""
                    SELECT CASE WHEN
                        HAS_PERMS_BY_NAME({table}, 'OBJECT', 'SELECT') = 1 AND
                        HAS_PERMS_BY_NAME({table}, 'OBJECT', 'INSERT') = 1 AND
                        HAS_PERMS_BY_NAME({table}, 'OBJECT', 'UPDATE') = 1 AND
                        HAS_PERMS_BY_NAME({table}, 'OBJECT', 'DELETE') = 1
                    THEN 1 ELSE 0 END AS [Value]
                    """).SingleAsync(cancellationToken);
                if (allowed != 1)
                {
                    logger.LogError("Journal runtime identity lacks required table data permissions.");
                    return HealthCheckResult.Unhealthy("Required journal data permissions are missing.");
                }
            }

            return HealthCheckResult.Healthy(data: new Dictionary<string, object>
            {
                ["KnowledgeFullTextIndex"] = knowledgeRepository.GetIndexState().ToString()
            });
        }
        catch (Exception exception) when (exception is DbException or TimeoutException or InvalidOperationException)
        {
            logger.LogError("Journal database readiness failed ({FailureType}).", exception.GetType().Name);
            return HealthCheckResult.Unhealthy("Journal database verification failed.");
        }
    }
}
