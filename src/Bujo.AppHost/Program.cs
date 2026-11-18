var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", secret: true);
var sqlServer = builder.AddSqlServer("sql", password: sqlPassword)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var journalDatabase = sqlServer.AddDatabase("journal");

var journalHost = builder.AddProject<Projects.Bujo_Host>("journal-host")
    .WithReference(journalDatabase)
    .WaitFor(journalDatabase)
    .WithEnvironment("Auth__TenantId", builder.Configuration["Auth:TenantId"] ?? string.Empty)
    .WithEnvironment("Auth__Audience", builder.Configuration["Auth:Audience"] ?? string.Empty)
    .WithEnvironment("Auth__AllowedPrincipals", builder.Configuration["Auth:AllowedPrincipals"] ?? string.Empty)
    .WithEnvironment("Mcp__PublicBaseUrl", builder.Configuration["Mcp:PublicBaseUrl"] ?? string.Empty)
    .WithArgs("--migrate");

foreach (var origin in builder.Configuration.GetSection("Mcp:AllowedOrigins").GetChildren())
{
    journalHost.WithEnvironment($"Mcp__AllowedOrigins__{origin.Key}", origin.Value ?? string.Empty);
}

builder.Build().Run();
