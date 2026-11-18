using Bujo.Infrastructure;
using Bujo.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using GenericHost = Microsoft.Extensions.Hosting.Host;

namespace Bujo.Host;

internal static class MigrationJob
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("Journal migration starting.");
        var stage = "configuration";
        try
        {
            var builder = GenericHost.CreateApplicationBuilder(args);
            if (!builder.Environment.IsProduction())
            {
                Console.Error.WriteLine("Journal migration failed: the job requires the Production environment.");
                return 1;
            }

            var connectionString = builder.Configuration.GetConnectionString("journal");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine("Journal migration failed: journal SQL configuration is missing.");
                return 1;
            }

            // Report only categorical diagnostics: provider messages can include SQL target and identity details.
            builder.Logging.ClearProviders();
            builder.Services.AddJournalPersistence(connectionString);
            using (var host = builder.Build())
            {
                stage = "database migration";
                await using var scope = host.Services.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JournalDbContext>>();
                await using var context = await factory.CreateDbContextAsync();
                await context.Database.MigrateAsync();
            }

            Console.WriteLine("Journal migration completed.");
            return 0;
        }
        catch (SqlException error)
        {
            Console.Error.WriteLine($"Journal migration failed during {stage}: SQL error {error.Number}.");
        }
        catch (RetryLimitExceededException error) when (error.InnerException is SqlException sqlError)
        {
            Console.Error.WriteLine($"Journal migration failed during {stage}: retries exhausted (SQL error {sqlError.Number}).");
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine($"Journal migration failed during {stage}: invalid argument or configuration.");
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"Journal migration failed during {stage}: invalid operation.");
        }

        return 1;
    }
}
