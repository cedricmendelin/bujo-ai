# Local development

The application targets .NET 11. Install the .NET 11 SDK and Podman before
starting the local SQL Server and ASP.NET Core host through Aspire.

## Podman and Aspire

On macOS and Windows, initialize and start the Podman machine:

```sh
podman machine init
podman machine start
```

On Linux, install Podman and enable its rootless socket:

```sh
systemctl --user enable --now podman.socket
```

Initialize the AppHost's local user-secrets store and provide an SQL password
there. Do not put credentials in the repository or a committed environment file:

```sh
dotnet user-secrets init --project src/Bujo.AppHost
dotnet user-secrets set "Parameters:sql-password" "<local-only-password>" --project src/Bujo.AppHost
dotnet user-secrets set "Auth:TenantId" "<tenant-guid>" --project src/Bujo.AppHost
dotnet user-secrets set "Auth:Audience" "<api-application-id-or-uri>" --project src/Bujo.AppHost
dotnet user-secrets set "Auth:AllowedPrincipals" "<tenant-guid>:<approved-object-guid>" --project src/Bujo.AppHost
```

Run the local host and SQL container with Podman:

```sh
ASPIRE_CONTAINER_RUNTIME=podman dotnet run --project src/Bujo.AppHost
```

The AppHost's SQL data volume persists across local runs. Its `--migrate`
argument applies EF migrations only while the host runs in Development. The
production host does not run migrations at startup. The Aspire dashboard shows
the host and SQL resource status.

For Windows PowerShell, set the runtime with
`$env:ASPIRE_CONTAINER_RUNTIME = "podman"` before `dotnet run`.

## SQL-backed integration test

The integration suite includes a SQL Server test that migrates an empty database
and verifies that a captured, moved, and completed item remains resolvable from
a fresh repository instance. CI starts a disposable SQL Server and supplies the
test connection string as an ephemeral environment variable.

To run this test locally, start a disposable SQL Server with Podman or Docker,
then set `BUJO_SQL_TEST_CONNECTION` in your shell to a connection string for
that instance's `master` database. The login must be able to create and drop
databases. Run `dotnet test tests/Bujo.Integration.Tests/Bujo.Integration.Tests.csproj`;
the test fails rather than skips when the variable or SQL Server is unavailable.
The CI job uses a pinned SQL Server container on an x64 runner. The documented
container architecture is validated on x64; ARM host behavior is not yet verified.

Host-restart behavior, transactional failure rollback, and database concurrency
tests remain outstanding.
