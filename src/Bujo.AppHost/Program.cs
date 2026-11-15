var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", secret: true);
var sqlServer = builder.AddSqlServer("sql", password: sqlPassword)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);
var journalDatabase = sqlServer.AddDatabase("journal");

builder.AddProject<Projects.Bujo_Host>("journal")
    .WithReference(journalDatabase)
    .WaitFor(journalDatabase)
    .WithArgs("--migrate");

builder.Build().Run();
