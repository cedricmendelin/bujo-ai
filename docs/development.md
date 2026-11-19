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

The AppHost already declares a user-secrets store. Provide a local SQL password
there (at least eight characters with uppercase, lowercase, digits, and symbols).
Do not put credentials in the repository or a committed environment file:

```sh
dotnet user-secrets set "Parameters:sql-password" "<local-only-password>" --project src/Bujo.AppHost
dotnet user-secrets set "Auth:TenantId" "<tenant-guid>" --project src/Bujo.AppHost
dotnet user-secrets set "Auth:Audience" "<api-application-client-id>" --project src/Bujo.AppHost
dotnet user-secrets set "Auth:AllowedPrincipals" "<approved-object-guid>" --project src/Bujo.AppHost
```

For Entra v2 access tokens, `Auth:Audience` is the API registration's
Application (client) ID, not the browser-client ID or scope URI.
The current local implementation expects `Auth:AllowedPrincipals` to contain
bare user Object IDs; this format is a proposal pending owner confirmation.
`Auth:TenantId` supplies the tenant boundary: a token is allowed only when its
tenant matches that setting and its user Object ID is listed. Use user Object
IDs from **Entra ID > Users**, not app-registration Object IDs. Do not run this
build against owner-managed settings in the old `tenant-id:object-id` format or
change those settings until the owner confirms the contract and authorizes any
required update. This change does not modify live settings.

From the repository root, run the local host and SQL container with Podman:

```powershell
dotnet dev-certs https --trust
dotnet run --project src\Bujo.AppHost --launch-profile https
```

The checked-in `https` launch profile selects Podman and Development mode,
opens the dashboard at `https://localhost:17000`, and configures Aspire's
telemetry and resource-service endpoints. Use the dashboard login URL printed
in the terminal if prompted for a token. In Visual Studio, select
`Bujo.AppHost` as the startup project and the `https` launch profile, then run.
Do not launch the executable in `bin` directly; use the project launch profile
or `dotnet run` so Aspire receives its startup configuration.

If startup appears stuck, check the following:

- `podman machine list` must show a running machine and `podman ps` must succeed.
  Run `podman machine start` if the existing machine is stopped; do not
  initialize another machine on every run.
- **Unresolved parameters** in the dashboard means `Parameters:sql-password`
  has not been supplied. Set it in user secrets and restart AppHost.
- The first SQL startup pulls the container image and creates the database.
  Inspect the `sql` and `journal-host` console logs in the dashboard if they
  remain unhealthy. Existing SQL volumes retain their original password;
  changing the parameter does not reset the stored SQL login password.
- Stop the previous AppHost run before starting another one; the launch
  profile uses fixed localhost ports.

Starting the dashboard, database, and anonymous host pages does not require
Entra configuration. Protected API/MCP calls still require the real API auth
settings above. For browser sign-in, also set the following in AppHost user
secrets; AppHost forwards these settings to `journal-host`:

```powershell
dotnet user-secrets set "Auth:Pwa:ClientId" "<web-client-application-id>" --project src\Bujo.AppHost
dotnet user-secrets set "Auth:Pwa:ClientSecret" "<client-secret-value>" --project src\Bujo.AppHost
dotnet user-secrets set "Auth:Pwa:Scope" "<API-application-ID-URI>/journal.access" --project src\Bujo.AppHost
```

Use the exact delegated scope URI from the API registration's **Expose an API**
page. Terraform registers `https://localhost:7168/signin-oidc` as a **Web**
redirect URI on the PWA client. Restart AppHost after changing secrets.
If PWA settings are absent from AppHost, it leaves any host-specific PWA
configuration untouched. See [the authentication guide](auth.md) for the full
requirements; local startup does not configure Entra or bypass authentication.

An immediate redirect to `/auth/error` before reaching Microsoft sign-in can
mean required auth configuration is missing or malformed. Browser-console CSP
warnings about `browserLinkSignalR` or `aspnetcore-browser-refresh.js` are
separate: Visual Studio injects development tooling that tries to connect to
other local ports, which the app's `connect-src 'self'` policy blocks. Disable
Browser Link and browser refresh in the IDE if needed; do not broaden the
application CSP to allow arbitrary localhost connections.

For authenticated MCP setup, the exact-Origin allowlist, and client-specific
configuration examples, see the [remote MCP guide](mcp.md). Do not put access
tokens, journal content, or local connection details in client config files.

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

## Container image and SQL durability validation

Build the application image locally without deploying it:

```sh
docker build --tag bujo-ai:local .
# Podman can be used instead:
podman build --tag bujo-ai:local .
```

Run the normal solution checks with the pinned SDK:

```sh
dotnet build Bujo.slnx --configuration Release
dotnet test Bujo.slnx --configuration Release
```

SQL-backed durability tests are opt-in and use a real SQL Server database.
Start the local Aspire AppHost as described above, then provide
`BUJO_SQL_DURABILITY_CONNECTION` with a SQL Server connection string whose
credential is held outside the repository and whose login can create/drop a
temporary test database. Obtain the local SQL connection details from the
running Aspire resource and set the variable only in the test process
environment. Do not print or commit the connection string. The test
creates a uniquely named database, applies the checked-in migrations, exercises
the production EF SQL Server provider, and drops the database afterward.

```sh
dotnet test tests/Bujo.Integration.Tests/Bujo.Integration.Tests.csproj \
  --configuration Release --filter "Category=SqlDurability"
```

These tests are not part of required CI: the SQL Server bootstrap was removed
at the owner's request. A successful local SQL Server test does not establish
Azure SQL parity or prove production backup/restore behavior. Keep the Aspire
AppHost compatibility caveat below; do not infer vendor certification from a
local run.

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

## PWA journal UI and rendering proof (issue #124)

The current #124 implementation renders Today at `/` for approved sessions and
provides Today, Upcoming, Capture, Lists, and item detail/history workflows
over the shared application service. Successful sign-in returns to `/`.
`/rendering-proof` remains under Diagnostics and exercises Interactive Server
without reading or writing journal data. `/auth/sign-in`, `/auth/signed-out`,
`/auth/denied`, and `/auth/error` are safe anonymous account pages. API
endpoints still require bearer tokens; signing into the browser does not
authorize an API request.

The local Podman/HTTPS/user-secrets and PWA-setting-forwarding fixes are merged
in [PR #125](https://github.com/cedricmendelin/bujo-ai/pull/125); they do not
add journal screens or authorize Entra changes. Issue #10 was closed after the
partial rendering proof, while [issue #124](https://github.com/cedricmendelin/bujo-ai/issues/124)
tracks the implementation and remaining acceptance evidence in the
[delivery matrix](delivery-matrix-124.md). A prior owner comment allowed the
limited proof to unblock UI work; final owner/Architect review remains pending.
It does not establish live sign-in, installation, or journal-persistence
verification.

The manifest and icons support an installable online-only journal. The service
worker caches only explicitly listed public static assets, never navigations,
account pages, journal responses, or API responses. There is no offline capture,
write queue, or automatic replay. The journal does not show network status
banners. A circuit loss shows a short recovery message; if the circuit cannot
recover, the app offers Reload. Saves still require server confirmation. The local browser
suite uses a synthetic identity and in-memory repository; it does not verify
SQL persistence across a host restart or contact Entra.

Run the browser proof tests with the existing .NET test tooling:

```sh
dotnet build Bujo.slnx --configuration Release
pwsh tests/Bujo.E2E.Tests/bin/Release/net11.0/playwright.ps1 install --with-deps chromium
dotnet test tests/Bujo.E2E.Tests/Bujo.E2E.Tests.csproj --no-build --configuration Release
```

CI installs Chromium before running the test projects. The browser suite uses a
real local Kestrel server and authentication configured **only inside the test
host**; it does not add a production sign-in bypass or contact Entra. The suite
must fail rather than skip if its browser is unavailable. The SQL persistence
integration suite was removed at the owner's request; this browser suite does
not establish SQL-backed persistence across a host restart.

### Owner-operated proof gate

Interactive Server with static SSR where appropriate is Accepted in
[ADR 0001](adr/0001-technology-baseline.md) as of 2026-11-17. The rendering proof
provides implementation evidence for that decision; it is not a request to
select a render mode. The PR #108 handoff required owner and Architect review
before UI work. A prior owner comment allowed implementation to start, but the
final owner/Architect review remains pending. Live Entra sign-in,
target-browser installation, cost and multi-replica recovery remain open
verification gates. Real
Entra sign-in and target-browser installation cannot be certified by the test
authentication scheme. Follow the approved proof runbook in [auth.md](auth.md);
do not access credentials, change registrations, grant consent, or deploy
without the owner's authorization.

After the owner approves target browsers and authorizes deployment, check the
following with synthetic text only:

1. Sign in through Entra as the approved account. Verify a non-approved account
   cannot reach `/rendering-proof`, and that anonymous account/error pages show
   no protected information.
2. Install the shell in each owner-approved target browser and launch it from
   its installed icon. Chrome on Windows desktop and Chrome on Android are
   proposed targets, not approved selections. Record browser/platform and
   observed installation behavior.
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
