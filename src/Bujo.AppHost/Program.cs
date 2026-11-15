Console.WriteLine("Hello, World!");
var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", secret: true);
var sqlServer = builder.AddSqlServer("sql", password: sqlPassword)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var journalDatabase = sqlServer.AddDatabase("journal");

builder.AddProject<Projects.Bujo_Host>("journal")
    .WithReference(journalDatabase)
    .WaitFor(journalDatabase)
    .WithEnvironment("Auth__AllowTestScheme", "true")
    .WithEnvironment("Auth__TenantId", "00000000-0000-0000-0000-000000000001")
    .WithEnvironment("Auth__AllowedPrincipals", "00000000-0000-0000-0000-000000000001:00000000-0000-0000-0000-000000000002")
    .WithArgs("--migrate");

builder.Build().Run();
