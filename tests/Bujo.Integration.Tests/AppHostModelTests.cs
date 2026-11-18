using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Bujo.Integration.Tests;

public sealed class AppHostModelTests
{
    [Fact]
    public async Task AppHostBuildsWithDistinctJournalDatabaseAndHostResources()
    {
        var entryPoint = Assembly.Load("Bujo.AppHost").EntryPoint!.DeclaringType!;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync(entryPoint);

        Assert.Contains(builder.Resources, resource =>
            resource.Name == "journal" && resource is SqlServerDatabaseResource);
        Assert.Contains(builder.Resources, resource =>
            resource.Name == "journal-host" && resource is ProjectResource);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppHostForwardsConfiguredPwaSettingsWithoutOverridingHostDefaults(bool configured)
    {
        var settings = new Dictionary<string, string>
        {
            ["ClientId"] = "00000000-0000-0000-0000-000000000003",
            ["ClientSecret"] = "synthetic-client-secret",
            ["Scope"] = "api://test-api/journal.access"
        };
        string[] args = configured
            ? settings.Select(setting => $"--Auth:Pwa:{setting.Key}={setting.Value}").ToArray()
            : [];
        var entryPoint = Assembly.Load("Bujo.AppHost").EntryPoint!.DeclaringType!;
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            entryPoint, args, (_, options) => options.EnvironmentName = "Testing");
        var host = Assert.Single(builder.Resources.OfType<ProjectResource>(),
            resource => resource.Name == "journal-host");
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, host);
        foreach (var annotation in host.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        foreach (var setting in settings)
        {
            var name = $"Auth__Pwa__{setting.Key}";
            if (configured)
                Assert.Equal(setting.Value, context.EnvironmentVariables[name]);
            else
                Assert.False(context.EnvironmentVariables.ContainsKey(name));
        }
    }
}
