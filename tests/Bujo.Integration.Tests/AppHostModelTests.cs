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
}
