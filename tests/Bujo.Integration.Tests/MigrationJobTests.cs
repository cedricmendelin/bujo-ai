using System.Diagnostics;
using Bujo.Host;
using Microsoft.Data.SqlClient;

namespace Bujo.Integration.Tests;

public sealed class MigrationJobTests
{
    [Fact]
    public void SqlClient_can_resolve_the_managed_identity_provider()
    {
        Assert.NotNull(SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity));
    }

    [Theory]
    [InlineData("Development", "the job requires the Production environment")]
    [InlineData("Production", "invalid argument or configuration")]
    public async Task Migration_job_reports_safe_failures(string environment, string diagnostic)
    {
        const string invalidConnection = "must-not-appear-in-logs";
        var hostAssembly = typeof(JournalDatabaseHealthCheck).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("--migrate-only");
        startInfo.Environment["DOTNET_ENVIRONMENT"] = environment;
        startInfo.Environment["ConnectionStrings__journal"] = invalidConnection;
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }

        var output = await stdout;
        var errors = await stderr;
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Journal migration starting.", output);
        Assert.DoesNotContain("Journal migration completed.", output);
        Assert.Contains("Journal migration failed", errors);
        Assert.Contains(diagnostic, errors);
        Assert.DoesNotContain(invalidConnection, output + errors);
        Assert.DoesNotContain("Unhandled exception", errors);
    }
}
