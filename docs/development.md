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

The integration tests currently verify API authorization with a test-only
authentication handler. SQL-backed migration, restart, rollback, and concurrency
tests are not yet implemented; the existing test suite does not prove Azure SQL
or SQL Server persistence.
