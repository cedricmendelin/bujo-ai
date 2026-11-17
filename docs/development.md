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
The Aspire dashboard shows the host and SQL resource status. Of the health
endpoints, only `/health/live` allows anonymous access; `/health/ready` is
protected by the approved-principal policy and returns no response body.

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

## PWA rendering proof (issue #10, plan v3)

The initial UI is a rendering proof, **not the journal UI**. The protected
`/rendering-proof` page exercises Interactive Server without reading or writing
journal data. `/auth/sign-in`, `/auth/signed-out`, `/auth/denied`, and `/auth/error`
are safe anonymous account pages. API endpoints still require bearer tokens;
signing into the browser does not authorize an API request.

The manifest and icons support an installable online-first shell. The service
worker caches only explicitly listed public static assets, never navigations,
account pages, journal responses, or API responses. There is no offline capture,
write queue, or automatic replay. Disconnecting preserves the current document
while the circuit remains recoverable; a host restart, circuit expiration, or
scale-to-zero can require a reload and lose unsaved input. A successful proof
roundtrip is not evidence that a journal item was saved.

Run the browser proof tests with the existing .NET test tooling:

```sh
dotnet build Bujo.slnx --configuration Release
pwsh tests/Bujo.E2E.Tests/bin/Release/net11.0/playwright.ps1 install --with-deps chromium
dotnet test tests/Bujo.E2E.Tests/Bujo.E2E.Tests.csproj --no-build --configuration Release
```

CI installs Chromium before running the test projects. The browser suite uses a
real local Kestrel server and authentication configured **only inside the test
host**; it does not add a production sign-in bypass or contact Entra. The suite
must fail rather than skip if its browser is unavailable. SQL-backed tests
continue to require the disposable SQL Server described above.

### Owner-operated proof gate

Plan v3 requires owner and Architect review of the rendering proof before
building the capture, lists, history, daily-start, and upcoming views. Real
Entra sign-in and target-browser installation cannot be certified by the test
authentication scheme. Follow the approved proof runbook in [auth.md](auth.md);
do not access credentials, change registrations, grant consent, or deploy
without the owner's authorization.

On the approved HTTPS deployment, check the following with synthetic text only:

1. Sign in through Entra as the approved account. Verify a non-approved account
   cannot reach `/rendering-proof`, and that anonymous account/error pages show
   no protected information.
2. Install the shell in each supported browser and launch it from its installed
   icon. Record browser/platform and observed installation behavior.
3. Use only the keyboard to enter proof text and invoke the roundtrip control.
   Disconnect networking: the banner must appear, interaction must be disabled,
   and text must remain visible without any success claim or queued action.
4. Reconnect and verify an explicit new interaction works. Repeat after a host
   restart; verify reload guidance rather than promising circuit recovery.
5. Inspect service-worker caches: only public shell assets may be present.
   Record sanitized results and request owner/Architect review in the PR.

Assume one host replica: circuits and server-side authentication sessions are
not shared across replicas. No replica settings, Azure resources, or deployment
configuration are changed here. Retained circuits consume server memory and
require an online host; no live scale-to-zero, cost, or production performance
measurements have been made.
