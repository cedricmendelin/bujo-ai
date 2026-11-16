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
production host does not run migrations at startup; production deployment uses
the dedicated `--migrate-only` job entry point before promoting an app revision.
The Aspire dashboard shows the host and SQL resource status. Only
`/health/live` allows anonymous access;
`/health/ready` is protected by the approved-principal policy and returns no
response body.

Production sets `Deployment__VerifyDatabaseOnStart=true`. Before serving HTTP,
the runtime identity must be able to read the migration history, find every
migration required by that release, and select/insert/update/delete every
journal table. The check inspects permissions without changing journal data or
requiring DDL. It fails startup visibly; public liveness cannot pass while that
check is failing. Database readiness remains protected and reveals no response
body.

The same verification can run without opening an HTTP listener:

```powershell
dotnet run --project src\Bujo.Host -- --verify-database
```

Provide the existing private `ConnectionStrings:journal` configuration first.
Verification and migration flags are mutually exclusive. `--verify-database`
never applies migrations; `--migrate-only` remains the dedicated production
migration entry point.

For Windows PowerShell, set the runtime with
`$env:ASPIRE_CONTAINER_RUNTIME = "podman"` before `dotnet run`.

## SQL-backed integration test

The integration suite migrates an empty database and verifies normalized
journal rows, multi-hop moves across repository restarts, idempotent retries,
close/move races, transactional rollback, and concurrent writes. CI starts a
disposable SQL Server and supplies the test connection string as an ephemeral
environment variable.

To run this test locally, start a disposable SQL Server with Podman or Docker,
then set `BUJO_SQL_TEST_CONNECTION` in your shell to a connection string for
that instance's `master` database. The login must be able to create and drop
databases. Run `dotnet test tests/Bujo.Integration.Tests/Bujo.Integration.Tests.csproj`;
the test fails rather than skips when the variable or SQL Server is unavailable.
The CI job uses a pinned SQL Server container on an x64 runner. The documented
container architecture is validated on x64; ARM host behavior is not yet verified.

Startup/readiness tests also cover an unrelated migration-history row,
insufficient runtime table permissions, a reader/writer user without DDL,
unmigrated startup rejection, and unchanged API authorization after verification.

## Compatibility evidence

The repository pins SDK 11.0.100 and Aspire 13.6.5. Local validation used that
SDK with Podman and real SQL Server: the solution build and all 83 tests passed
without skips, including the Aspire AppHost model test. Host Release publish
contains no Aspire, AppHost or ServiceDefaults assemblies. These are local
observations, not a blanket vendor support claim.

The official [Aspire prerequisites](https://aspire.dev/get-started/prerequisites/)
name .NET 10 for C# AppHosts, describe hosted apps targeting .NET 8 or later,
and list Podman as a supported alternative. The [13.6.5 release
notes](https://github.com/microsoft/aspire/releases/tag/v13.6.5) do not explicitly
certify this repository's .NET 11 AppHost combination. That narrower official
compatibility evidence remains an issue #5/B3 blocker; do not silently change
the approved application runtime or declare it resolved from a local run.
