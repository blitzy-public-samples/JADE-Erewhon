# Erewhon operations note

This note covers running the Erewhon store in `ErewhonModern/`: one ASP.NET Core 8 process (`src/Erewhon.Api`) over one PostgreSQL 16 database. It replaces the JADECare Start utilities and the JADE exception log.

- Commands run from `ErewhonModern/` unless stated otherwise.
- Values in angle brackets (`<connection string>`, `<database>`, `<data dir>`, `<backup dir>`, `<file>`, `<host>`, `<port>`, `<id>`) are placeholders. In the commands, a placeholder for a file or directory path is written in double quotes, so that a path with spaces stays one argument. No host, user, password or connection string is given here or committed anywhere (D-28).
- Decision IDs (`D-xx`) refer to `docs/migration/decision-log.md`, which holds the reasons. Behaviour IDs (`B-xx`, `Q-xx`, `EE-xx`) refer to `docs/migration/behaviour-catalogue.md`.

## 1. Prerequisites

| Component | Version | Needed for |
|---|---|---|
| .NET SDK | 8.0.426, pinned in `global.json` with `rollForward: latestPatch` (ASP.NET Core and .NET runtimes 8.0.32) | Building and running the API, the `migrate` and `seed` verbs, `dotnet ef`, the .NET tests |
| .NET local tools | `dotnet-ef` 8.0.32 and `dotnet-svcutil` 8.0.0, listed in `.config/dotnet-tools.json`; installed by `dotnet tool restore` | Migrations; regenerating the SOAP client |
| Node.js | 24.22.0 (`web/package.json` `engines`: `>=24.22.0 <25`) | Building the React client into `src/Erewhon.Api/wwwroot`, and the React tests. The running API does not need it |
| PostgreSQL | 16 server; client tools 16.15 | The database. This note uses `psql`, `pg_dump`, `pg_restore`, `pg_basebackup`, `pg_verifybackup`, `pg_amcheck`, `vacuumdb`, `reindexdb`, `pg_isready`, `pg_ctl`, `pg_waldump` and `pg_checksums` |

No JADE installation, JADE runtime, JADE INI file or JADECare Start component is needed.

## 2. The process

### 2.1 Endpoints

`src/Erewhon.Api` is the only server process. It serves:

| Path | Serves |
|---|---|
| `/api/rest/**` | The REST operations (`docs/rest-api.md`) |
| `/api/app/**` | The endpoints behind the React shop and admin screens. Every route except `GET /api/app/logon-options`, `POST /api/app/sessions` and `POST /api/app/setup/load-data` needs the `X-Erewhon-Session` header of a registered session and answers 401 without one (D-13, D-39) |
| `/soap/ErewhonWebService`, `/soap/ErewhonWebServiceAdmin` | The SOAP 1.1 services. The WSDL is at `?wsdl`, with addresses taken from the request's `Host` header (D-05, D-124) |
| `/hubs/erewhon` | The SignalR hub. Its `session` query value must name a registered session (D-12, D-39) |
| `/swagger`, `/swagger/rest/swagger.json`, `/swagger/app/swagger.json` | The Swagger UI and the two OpenAPI documents, served in every environment (D-25, D-124) |
| `/healthz` | The database health check (section 5) |
| Any other path | A static file from `src/Erewhon.Api/wwwroot`. A path without a file extension that no row above answers gets 404 with an empty body when its first segment is `api`, `soap`, `hubs`, `swagger` or `healthz`, compared case-insensitively, and `index.html` (the React client) otherwise (D-124) |

`wwwroot` is written by the React build, `(cd web && npm ci && npm run build)`. It is not committed.

### 2.2 Starting and stopping

```bash
dotnet run --project src/Erewhon.Api
```

- The launch profile `Erewhon.Api` (`src/Erewhon.Api/Properties/launchSettings.json`) listens on `http://localhost:5080` and sets `ASPNETCORE_ENVIRONMENT=Development`, replacing any value the shell gives that variable. The environment is then `Development`, so `appsettings.Development.json` applies, unless `DOTNET_ENVIRONMENT` or `--environment` names another.
- `--urls <url>` sets another listening address. `--no-launch-profile` runs without the profile: the environment then comes from the selectors in the next item, and the address is the ASP.NET Core default.
- The environment is the first name given by, in this order: the switch `--environment <name>` (or `--environment=<name>`), placed after `--` with `dotnet run`, for the web host only; the environment variable `DOTNET_ENVIRONMENT`; the environment variable `ASPNETCORE_ENVIRONMENT`. It is `Production` when none of them gives one.
- The `migrate` and `seed` verbs take the environment from the two variables only, in the same order and with the same `Production` fallback; the launch profile's `ASPNETCORE_ENVIRONMENT` applies to them too. They do not read `--environment`: `migrate` ignores it; after the `seed` directory operand it counts as extra operands, so `seed` writes its example command line and exits with code 1; placed before either verb, it makes the process start the web host instead (D-124).
- `dotnet run` starts the process in the directory it is run from, so a relative path on its command line, such as the `seed` operand, is resolved from that directory (D-156).
- The content root is the folder that holds `Erewhon.Api.csproj`, `src/Erewhon.Api`, whenever the process runs from that project's build output, as it does under `dotnet run`: `appsettings*.json`, `wwwroot` and relative configured paths are read from it. `--contentRoot <dir>` (after `--` with `dotnet run`, web host only) or the environment variable `ASPNETCORE_CONTENTROOT` or `DOTNET_CONTENTROOT` sets another; a process run from anywhere else, such as a published copy, takes its current directory as the content root (D-156).
- Ctrl+C or SIGTERM stops the host; the process then exits with code 0, also when the signal arrives while the host is starting.
- When building, starting, running, stopping or disposing the host throws, for the web host or for a `migrate` or `seed` verb, the process writes one `Fatal` event `ErewhonHostFailed` (section 6.2) and exits with code 1, with no runtime crash and no core dump. Examples: `Erewhon__Closure__TickMs=abc` (the closure service cannot start), a `Serilog` level that is not a level (the logger cannot be built) (D-157). A `dotnet ef` command, which stops the host while it is built, writes no such event (D-157).

### 2.3 State held in process memory

Sessions (`SessionRegistry`), carts (`InMemoryCartStore`) and closure schedules (`ClosureScheduleRegistry`) live in the memory of the one process (D-14). A restart therefore:

- ends every session: the next app request gets 401 and the React client returns to its logon page;
- empties every cart;
- stops every closure schedule.

A second API process does not share this state. Soft edit locks are stored in the database and survive a restart (section 7.4).

## 3. Configuration

Configuration uses the standard ASP.NET Core sources. Each source overrides the ones before it:

1. `src/Erewhon.Api/appsettings.json`;
2. `src/Erewhon.Api/appsettings.<environment>.json` (`appsettings.Development.json` is the only one committed);
3. environment variables, where `__` separates the sections of a key (`Erewhon__Locks__TimeoutMs`);
4. command-line switches (`--Erewhon:Locks:TimeoutMs=<ms>`), for the web host only.

In item 2, `<environment>` is the environment name, chosen as section 2.2 describes.

The `migrate` and `seed` verbs read no command-line switches, so their values come from the files and environment variables (D-124).

| Key | Environment variable | Default | Read by | Effect |
|---|---|---|---|---|
| `ConnectionStrings:Erewhon` | `ConnectionStrings__Erewhon` | Empty (committed empty in `appsettings.json`) | The API host, each time it builds a database context; the health check, on every request; the `migrate` and `seed` verbs. `DesignTimeDbContextFactory` (`dotnet ef`) reads the environment variable only (D-74) | The PostgreSQL connection of the store. Empty: `/healthz` answers 503, every database operation, `migrate` and `seed` included, fails with "ConnectionStrings:Erewhon is not set.", and `dotnet ef database update` fails with the provider's message, while `dotnet ef migrations add` and `migrations script` still run (D-28, D-118) |
| `ConnectionStrings:ErewhonTests` | `ConnectionStrings__ErewhonTests` (read from the environment only) | Not set | `PostgresDatabase` in `tests/Erewhon.Testing` (every .NET test assembly), `tests/Erewhon.FixtureHost` (the Vitest fixture bridge), `web/e2e/start-e2e-server.mjs` (Playwright) | The base connection of the test databases (section 3.2) (D-26, D-47) |
| `Erewhon:Seed:DataDirectory` | `Erewhon__Seed__DataDirectory` | `../../../Erewhon/DataFiles`, which from the content root `src/Erewhon.Api` is the repository's `Erewhon/DataFiles` | `POST /api/app/setup/load-data`, read once at start-up | The directory the interactive load reads. Absent or empty gives the default; a relative path is resolved from the content root; the directory is checked only when a load runs (error 500004; error 1035 when the resolved path, followed by `/`, is longer than 301 characters) (D-18, D-114, D-117, D-118). The `seed` verb takes its directory from its operand instead |
| `Erewhon:Locks:TimeoutMs` | `Erewhon__Locks__TimeoutMs` | `10000` | `PostgresEntityLocks`, read once at start-up | The `lock_timeout`, in milliseconds, of every transaction-agent advisory lock and of each tender-closure row lock. Absent, empty, not an integer, 0 or negative gives 10000; a value that is present and not empty but gives 10000 is also logged once at start-up, by the web host and by each verb, as the `Warning` event `ErewhonLockSettingRejected` (section 6.2) (D-07, D-118, D-159, EE-14) |
| `Erewhon:Locks:AllocationTimeoutMs` | `Erewhon__Locks__AllocationTimeoutMs` | `10000` | `PostgresEntityLocks`, read once at start-up | The `lock_timeout`, in milliseconds, of each item-code allocation attempt. Three attempts run 100 ms apart; the third failure gives error 500001. Absent, empty, not an integer, 0 or negative gives 10000; a value that is present and not empty but gives 10000 is also logged once at start-up, by the web host and by each verb, as the `Warning` event `ErewhonLockSettingRejected` (section 6.2) (D-08, D-118, D-159, B-03, EE-14) |
| `Erewhon:Closure:TickMs` | `Erewhon__Closure__TickMs` | `1000` | `TenderClosureScheduleService`, read once when it starts | The length of one count-down second of a closure schedule, in milliseconds. Absent, 0 or negative gives 1000; a value that is not an integer stops the host from starting (D-30, D-123, EE-06) |
| `Serilog` (section) | `Serilog__MinimumLevel__Default`, `Serilog__MinimumLevel__Override__<category>`, … | Section 3.3 | The host's Serilog logger | Log levels, the console sink and its formatter (D-25) |
| `AllowedHosts` | `AllowedHosts` | `*` | ASP.NET Core host filtering | Accepted `Host` header values |

Tests and tooling may shorten the three timing settings; the defaults are the values in the table (EE-06).

### 3.1 Connection string

```bash
export ConnectionStrings__Erewhon='<connection string>'
```

`<connection string>` is an Npgsql connection string such as `Host=<host>;Port=<port>;Database=<database>;Username=<user>`. Npgsql also reads `PGUSER`, `PGPASSWORD` and the password file named by `PGPASSFILE` (default `~/.pgpass`) for values the connection string leaves out. The PostgreSQL client tools read `PGHOST`, `PGPORT`, `PGUSER`, `PGDATABASE` and the same password file.

Schema changes are made only through EF Core migrations (section 4.3) (D-28).

### 3.2 Test databases

`ConnectionStrings__ErewhonTests` must name a role allowed to create databases. Its `Database` is the maintenance database used for `DROP DATABASE` and `CREATE DATABASE` (`postgres` when it names none), and it must not be one of the test databases. Each run first drops a test database with `WITH (FORCE)`, which ends every session connected to it. When PostgreSQL refuses that drop with SQLSTATE `42501` (insufficient privilege), the run terminates the role's own other sessions on the database and drops it without `FORCE`, which also ends any autovacuum worker connected to it. A connected session of another role that is not an autovacuum worker, or a session of the role that reconnects between the termination and the second drop, then makes the reset fail with SQLSTATE `55006` (object in use), so end such sessions before a run (D-136). The run then recreates the database and applies the migrations:

| Database | Used by |
|---|---|
| `erewhon_test_<assembly>`: the assembly name in lower case with each `.` replaced by `_`, for example `erewhon_test_erewhon_application_tests` | Each .NET test assembly (D-26, D-80) |
| `erewhon_test_vitest` | The Vitest fixture bridge, `tests/Erewhon.FixtureHost serve` (D-47) |
| `erewhon_test_e2e` | The Playwright start script `web/e2e/start-e2e-server.mjs`, which also passes it to the API as `ConnectionStrings__Erewhon` (D-47) |

Database names are 1 to 63 lower-case letters, digits and underscores.

### 3.3 Logging settings

| Setting | `appsettings.json` | `appsettings.Development.json` |
|---|---|---|
| `Serilog:Using` | `Serilog.Sinks.Console` | — |
| `Serilog:MinimumLevel:Default` | `Information` | `Debug` |
| `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` | `Warning` | `Information` |
| `Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Hosting.Diagnostics` | `Warning` | `Warning` |
| `Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore` | `Warning` | `Information` |
| `Serilog:WriteTo` | `Console` with `formatter` `Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact` | — |
| `Serilog:Enrich` | `FromLogContext` | — |

The `Microsoft.AspNetCore.Hosting.Diagnostics` override keeps the framework's request-starting and request-finished events, which carry the query string, out of the log in every environment; the level policy is D-149.

## 4. Commands

### 4.1 First start on an empty database

```bash
dotnet tool restore
export ConnectionStrings__Erewhon='<connection string>'
dotnet run --project src/Erewhon.Api -- migrate
dotnet run --project src/Erewhon.Api -- seed "<data dir>"
(cd web && npm ci && npm run build)
dotnet run --project src/Erewhon.Api
```

### 4.2 Command-line verbs

The first argument after `--` selects a verb, compared case-sensitively. A verb runs in the built host without starting the web server or the closure schedule service (D-124), and returns an exit code.

| Command | What it does | Output | Exit code |
|---|---|---|---|
| `migrate` | Applies every pending EF Core migration of `src/Erewhon.Persistence` to the database of `ConnectionStrings:Erewhon`. Further operands are ignored | None | 0; 1 on failure |
| `seed "<data dir>"` | Runs the seed loader on `<data dir>` (D-18, D-114): checks the directory (error 500004 when it is empty, not a valid path or not an existing directory; otherwise error 1035 when `<data dir>` as given, trimmed of spaces and followed by `/` unless it ends with `/` or `\`, is longer than 301 characters; either error leaves the database unchanged), deletes the existing company with all its data and commits that, creates the company "Erewhon Investments Inc" (error 500003 when a company remains), loads the ten data files through the application services in one transaction, then closes the tenders due at the current server date | The loader's lines (`$Loading`, `$LoadedObject`, the `*Loaded` lines, then `$TenderSalesBuilt` or `$NoTendersToCloseAsAt`) on standard output; each line is also written to the log (section 6.2) (EE-11) | 0; 1 on failure |
| `seed` with no operand, or with more than one | Loads nothing | The `$ExampleCommandLine` message, verbatim JADE text (D-22) | 1 |
| `seed ""` | Loads nothing | None | 0 |

A verb that fails with an exception exits with 1 and writes one `ErewhonUnhandledException` error event, with the verb as its `Verb` property (section 6.2, D-146). The lock outcome is not logged: when the `seed` post-load closure cannot obtain a tender item's row lock within `Erewhon:Locks:TimeoutMs` (`55P03`), the verb exits with 1 without an event (B-38); the loaded data and the awards made before the lock stay. A wrong `seed` operand count is a usage error: `$ExampleCommandLine` on standard output, exit code 1 and no log event. A cancellation requested by the caller ends the verb without a log event.

Ctrl+C (SIGINT), SIGTERM or SIGQUIT while a verb runs cancels the verb (D-124). The database command in progress is cancelled, the open transaction is rolled back, the session ends, and the process exits with code 1, without the failing verb's error log event. A step that does not observe the cancellation finishes first; the item-code read of the allocator waits at most `Erewhon:Locks:AllocationTimeoutMs`. A `seed` cancelled after its purge has committed leaves no company, and running `seed` again loads the data. A second signal after the first ends the process at once, with exit code 130 for SIGINT or 143 for SIGTERM, without rolling back; PostgreSQL rolls the transaction back once the session notices the closed connection, which for a session waiting on a lock is when that wait ends; section 7.5 ends such a session sooner.

`<data dir>` is trimmed of spaces and resolved against the process's current directory, which under `dotnet run` is the directory the command is run from (section 2.2, D-156). From `ErewhonModern/`, `dotnet run --project src/Erewhon.Api -- seed ../Erewhon/DataFiles` names the repository's data files; an absolute path works from anywhere. The 301-character limit applies to `<data dir>` as given, after trimming and with its trailing separator, not to the resolved path: a short relative path is accepted however deep the current directory is, and a longer path to an existing directory fails with error 1035 before anything is deleted.

The interactive load, `POST /api/app/setup/load-data`, runs the same loader on `Erewhon:Seed:DataDirectory`. It runs only while no company exists and answers 409 otherwise (D-43, D-117).

### 4.3 Schema migrations

The migrations live in `src/Erewhon.Persistence/Migrations` (first migration `20261015000000_InitialSchema`). The `dotnet ef` commands take their connection from the `ConnectionStrings__Erewhon` environment variable (D-74). They build the host of `src/Erewhon.Api` and stop it before it starts, with no `ErewhonHostFailed` event (D-157).

```bash
# SQL script of every migration; each step is skipped on a database that already has it
dotnet ef migrations script --idempotent --project src/Erewhon.Persistence --startup-project src/Erewhon.Api --output "<file>.sql"

# Migrations known to the code and applied to the database
dotnet ef migrations list --project src/Erewhon.Persistence --startup-project src/Erewhon.Api

# Apply the pending migrations (the same as the migrate verb)
dotnet ef database update --project src/Erewhon.Persistence --startup-project src/Erewhon.Api

# Add a migration for a schema change
dotnet ef migrations add <Name> --project src/Erewhon.Persistence --startup-project src/Erewhon.Api
```

The database records applied migrations in `__EFMigrationsHistory`.

## 5. Health check

```bash
curl -i http://localhost:5080/healthz
pg_isready -h <host> -p <port>
```

`GET /healthz` runs the check `postgresql` on every request and answers with a `text/plain` body (D-25, D-124):

| Condition | Status | Body |
|---|---|---|
| A connection to `ConnectionStrings:Erewhon` opens and `SELECT 1` succeeds | 200 | `Healthy` |
| `ConnectionStrings:Erewhon` is absent or empty (no connection is attempted) | 503 | `Unhealthy` |
| The connection cannot be opened, or `SELECT 1` fails | 503 | `Unhealthy` |

Each run opens its own unpooled connection: `ConnectionStrings:Erewhon` with `Pooling=false` and `Multiplexing=false`. It runs `SELECT 1` and closes the connection, and never uses a connection of the API's Npgsql pool. So a pooled connection that PostgreSQL has ended does not turn `/healthz` to 503, and each request holds one PostgreSQL backend while it runs, counted against `max_connections` and the role's connection limit (D-158).

`/healthz` checks the API and its database connection together, replacing `KCCheckConnection`. `pg_isready` checks the PostgreSQL server alone and exits with 0 (accepting connections), 1 (rejecting connections), 2 (no response) or 3 (no attempt made, for example invalid parameters).

## 6. Logging

### 6.1 Output

Serilog writes one compact JSON (CLEF) object per event, one per line, to the process's standard output (D-25). The fields include `@t` (timestamp), `@mt` (message template), `@l` (level, omitted for `Information`), `@x` (exception, when present) and the event's properties; an event written with an event id carries it as `EventId` (`Id`, `Name`). Nothing is written to files: capture and rotate standard output with the process supervisor.

When the configuration has no `Serilog:WriteTo` entry, for example when the content root holds no `appsettings.json`, the host still writes every event in this format to standard output, and `Microsoft.AspNetCore`, `Microsoft.AspNetCore.Hosting.Diagnostics` and `Microsoft.EntityFrameworkCore` log from `Warning`, as in the base file, unless the configuration sets their level. A configuration with a `WriteTo` entry gets no extra sink (D-157). A host whose logger cannot be built writes its `ErewhonHostFailed` event in the same format.

### 6.2 Events

| Event | Level | Properties |
|---|---|---|
| Each HTTP request (`UseSerilogRequestLogging`) | `Information`; `Error` for status 500 and above or an exception | `RequestMethod`, `RequestPath`, `StatusCode`, `Elapsed` |
| An unhandled exception in a request (`ErrorOutcomeMiddleware`), event id 500 `ErewhonUnhandledException` | `Error` | `ErrorCode`, `ErrorText`, `ErrorItem`; `Role`, `AgentId` and `ClientId` when the request has a session. The session id is not logged (D-64). No event for a request the framework cannot bind or read (`BadHttpRequestException`, D-160) |
| A failed closure-schedule run (`TenderClosureScheduleService`), event id 500 `ErewhonUnhandledException` | `Error` | `ErrorCode`, `ErrorText`, `ErrorItem`, with `ClosureDate` from the log scope. The session id is not logged (D-64, D-123) |
| A failed closure-schedule tick (`TenderClosureScheduleService`) | `Error` | None; `@x` is the sanitized rendering below (D-64, D-123) |
| A failed closure-schedule SignalR send (`TenderClosureScheduleService`) | `Warning` | `HubMethod`; `@x` is the sanitized rendering below. The session id is not logged (D-64, D-123) |
| A failed `migrate` or `seed` verb (`CommandLineVerbs`), event id 500 `ErewhonUnhandledException` | `Error` | `ErrorCode`, `ErrorText`, `ErrorItem`, with `Verb` from the log scope. No event for the lock outcome or a usage error (D-146) |
| A seed loader line | `Information` | `SeedLine` |
| The host failed to be built, started, run, stopped or disposed (`ErewhonHost`), event id 1 `ErewhonHostFailed`; the process then exits with code 1 | `Fatal` | `ExitCode` (1); `@x` is the exception as thrown, with its messages, for example the configuration key whose value could not be converted (D-157) |
| A rejected `Erewhon:Locks:TimeoutMs` or `Erewhon:Locks:AllocationTimeoutMs` value: present and not empty, but not an integer, 0 or negative (`EntityLockSettings`), event id 600 `ErewhonLockSettingRejected`; once per key when the web host or a `migrate` or `seed` verb starts | `Warning` | `ConfigurationKey`, `RejectedValue`, `LockWaitMs` (always 10000) (D-159) |

The property values are:

- `ErrorCode`: the JADE error number (for example `500001`, `1035`), else the PostgreSQL SQLSTATE (for example `23505`, `40P01`, `08006` for a lost connection (D-62)), else the .NET `HResult`.
- `ErrorText`: the JADE error text of a JADE-numbered error, "ConnectionStrings:Erewhon is not set." when that setting is absent or empty (D-118), otherwise the full name of the exception type.
- `ErrorItem`: the field name of a 1035 "String too long" error, otherwise the `$BaseNotAvailable` text "Not available".
- `@x` of an unhandled-exception event, a failed closure-schedule tick and a failed closure-schedule SignalR send: the chain of exception types, each JADE error's number and text, the missing-connection-string text, and the stack traces. It carries no other exception message, PostgreSQL detail or connection text (D-64, D-123).

EF Core's connection open, close and failure events, its data-reader close and dispose events, and its command, query, save and transaction failure events are not written at any level, so in this log a database failure appears only as the events above (D-118).

When `seed` runs from the command line, standard output carries each loader line twice: once as plain text and once as its JSON event.

### 6.3 Outcomes and what is logged

| Condition | Outcome for the caller | `ErewhonUnhandledException` event |
|---|---|---|
| A transaction-agent lock is not obtained within `Erewhon:Locks:TimeoutMs` (SQLSTATE `55P03`) | The operation fails with `$CreateConflictedWithAnotherUser`, `$UpdateConflictedWithAnotherUser` or `$DeleteConflictedWithAnotherUser` in its error list | No |
| A tender-closure row lock is not obtained within `Erewhon:Locks:TimeoutMs` (`55P03`) | Status 500 with `{"kind": "lock", ...}`; from the `seed` verb, exit code 1 with the loaded data kept (D-146); awards already committed stay | No |
| A deadlock (`40P01`) | Status 500 with `{"kind": "deadlock", ...}`; the transaction is rolled back | Yes, `ErrorCode` `40P01` |
| Item-code allocation fails three times | Status 500 with `{"kind": "exception", ...}`, heading "Error 500001" (B-03) | Yes |
| Another database failure, for example `23505` (unique key), `23503` (foreign key) or a lost connection | Status 500 with `{"kind": "exception", ...}` carrying the SQLSTATE and the PostgreSQL message (EE-01) | Yes, `ErrorCode` is the SQLSTATE |
| A REST operation reads the company and no company exists (D-40, B-69). Outcomes decided before that read keep their status with or without a company: an invalid request to `getItemByCode`, `getItemsByPrice` or `getSale` is 400 with an empty body from the operation's input checks, and a request that fails model binding is 400 before any operation runs (D-109, D-110). `docs/rest-api.md` section "No company" gives the outcome of each operation | Status 500 with an empty body | No (D-63) |
| A REST `DTO*` field longer than its length (D-41) | Status 500 with an empty body | No (D-63) |
| A `postCreateAgent` or `postCreateClient` request whose body is absent, empty or JSON `null`, with or without a company. A chunked request that sends no data fails model binding instead (400). `docs/rest-api.md` sections `postCreateAgent` and `postCreateClient` give each outcome of the two operations | Status 500 with an empty body | No (D-63) |
| An app request (`/api/app/**`) whose JSON body is malformed or wrongly typed, or whose parameters cannot be bound, in every environment (D-105, D-160) | Status 400 with an empty body; the HTTP request event is at `Information` | No |
| A `BadHttpRequestException` that escapes the pipeline before the response has started, for example Kestrel rejecting a request body | The exception's own status with an empty body (D-160) | No |
| Any other unhandled exception | Status 500 with `{"kind": "exception", ...}` | Yes |
| An unhandled exception thrown after the response has started | The response ends early: nothing more is written and the request is aborted. The HTTP request event reports the status the response started with (D-64) | Yes, except for the tender-closure row-lock outcome and a `BadHttpRequestException` (D-160) |

The three lock outcomes are B-38 (D-07, D-64). Every response with status 500 also gets the HTTP request event at `Error` level.

### 6.4 PostgreSQL server log

The PostgreSQL server log is separate from the API log. It records each statement error, including `canceling statement due to lock timeout` and `deadlock detected` with the processes involved. With `log_lock_waits = on` it also records every lock wait longer than `deadlock_timeout`.

### 6.5 Replaced JADE exception log

`ErewhonBaseSchemaGlobal.zLogException` appended to `<JADE install>/../logs/erewhon/<application>.log` a line with the time, computer name and user name, then the exception details, the process stack and a separator line [Erewhon/ErewhonBaseSchema.scm:L545-600]. The `ErrorOutcomeMiddleware` event of section 6.2 replaces it; the error code, error text, error item and session user are its properties, and the stack is in `@x`.

### 6.6 No audit trail

No write is audited. Log events are written outside database transactions, so an event can exist for a request whose transaction rolled back (D-36).

## 7. Locks and concurrency

### 7.1 Advisory lock names

Each transaction-agent lock, and the item-code allocation lock, is a PostgreSQL advisory lock on a name of the form `<Owner>.<collection>:<ownerId>`, where `<ownerId>` is the `id` of the owning row. The database key is `hashtextextended(<name>, 0)` (D-07). Each lock is exclusive, and each wait is bounded by `lock_timeout`, which the API sets for the current transaction only.

| Lock name | Lifetime | Taken by |
|---|---|---|
| `Company.allAgents:<company id>` | Transaction | `AgentService` |
| `Company.allClients:<company id>` | Transaction | `ClientService` |
| `Company.allCountries:<company id>` | Transaction | `CountryService` |
| `Company.allItemCategoriesByName:<company id>` | Transaction | `ItemCategoryService` |
| `Company.allItems:<company id>` | Transaction; session, for the moment of a code allocation | `ItemService`; `ItemCodeAllocator`, which releases it right after reading the next code number (D-08) |
| `Company.allSalesByItem:<company id>` | Transaction | `ItemCategoryService`, `RetailSaleService`, `TenderSaleService`, checkout in `ShoppingCartService` |
| `Company.allTenderItemsBySaleDate:<company id>` | Transaction | `ItemCategoryService`, `ItemService` |
| `ItemCategory.allCommissionRates:<category id>` | Transaction | `CommissionRateService` |
| `ItemCategory.allItems:<category id>`, `Agent.allItems:<agent id>`, `Agent.allSoldItems:<agent id>`, `Region.allItems:<region id>` | Transaction | `ItemService` |
| `Country.allRegions:<country id>` | Transaction | `RegionService` |
| `Client.allRetailSales:<client id>` | Transaction | `RetailSaleService`, checkout |
| `Client.allTenderSales:<client id>` | Transaction | `TenderSaleService`, checkout |
| `Client.allTenders:<client id>`, `TenderItem.allTendersByOfferTime:<item id>`, `TenderItem.allTendersByTimeOffer:<item id>` | Transaction | `TenderService` |

A transaction lock is held until its transaction commits or rolls back. The key of a name:

```sql
SELECT hashtextextended('Company.allSalesByItem:<company id>', 0);
```

### 7.2 Holders and waiters of an advisory lock

`pg_locks` shows a bigint advisory key with its high 32 bits in `classid`, its low 32 bits in `objid` and `objsubid = 1`. Run in the store's database:

```sql
SELECT l.pid,
       l.granted,
       pg_blocking_pids(l.pid) AS blocked_by,
       a.application_name,
       a.state,
       a.xact_start,
       a.query
FROM pg_locks AS l
JOIN pg_stat_activity AS a ON a.pid = l.pid
WHERE l.locktype = 'advisory'
  AND l.objsubid = 1
  AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
  AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended('<lock name>', 0);
```

`granted = true` is the holder; `granted = false` is a waiter, and `blocked_by` names the process it waits for. Without the last condition the query lists every advisory lock, and `(l.classid::bigint << 32) | l.objid::bigint` is each lock's key.

### 7.3 Tender-closure row locks

Tender closure locks the `items` row of each due item with `SELECT ... FOR UPDATE`, in that item's own transaction, waiting at most `Erewhon:Locks:TimeoutMs` (D-09). The holder shows in `pg_locks` as a `RowShareLock` on the relation `items`; a waiter shows an ungranted lock on the holder's transaction id. Sessions that wait for any lock, with the sessions they wait for:

```sql
SELECT a.pid,
       pg_blocking_pids(a.pid) AS blocked_by,
       a.wait_event_type,
       a.wait_event,
       a.xact_start,
       a.query
FROM pg_stat_activity AS a
WHERE cardinality(pg_blocking_pids(a.pid)) > 0;
```

### 7.4 Soft edit locks

The first change in an admin edit dialog marks its row with `locked_by_session` (the session id) and `locked_timestamp`; closing or saving clears the mark. These are columns, not database locks (D-10). They are used on `agents`, `clients`, `companies`, `items`, `item_categories`, `commission_rates`, `countries` and `regions`. A mark left by a session that ended without closing its dialog, or by an API restart, keeps prompting `$ThingCurrentlyEdited` until it is cleared in the database:

```sql
SELECT 'agents' AS table_name, id, locked_by_session, locked_timestamp FROM agents WHERE locked_by_session IS NOT NULL
UNION ALL SELECT 'clients', id, locked_by_session, locked_timestamp FROM clients WHERE locked_by_session IS NOT NULL
UNION ALL SELECT 'companies', id, locked_by_session, locked_timestamp FROM companies WHERE locked_by_session IS NOT NULL
UNION ALL SELECT 'items', id, locked_by_session, locked_timestamp FROM items WHERE locked_by_session IS NOT NULL
UNION ALL SELECT 'item_categories', id, locked_by_session, locked_timestamp FROM item_categories WHERE locked_by_session IS NOT NULL
UNION ALL SELECT 'commission_rates', id, locked_by_session, locked_timestamp FROM commission_rates WHERE locked_by_session IS NOT NULL
UNION ALL SELECT 'countries', id, locked_by_session, locked_timestamp FROM countries WHERE locked_by_session IS NOT NULL
UNION ALL SELECT 'regions', id, locked_by_session, locked_timestamp FROM regions WHERE locked_by_session IS NOT NULL;

UPDATE <table> SET locked_by_session = NULL, locked_timestamp = NULL WHERE id = <id>;
```

### 7.5 Ending a blocking session

`SELECT pg_cancel_backend(<pid>);` cancels the process's current statement; `SELECT pg_terminate_backend(<pid>);` ends its connection. Ending a connection rolls back its transaction and releases its transaction and session advisory locks and its row locks.

## 8. JADECare Start equivalents

Erewhon uses none of the 26 JADECare Start applications, and none is ported (D-25). Each maps to a PostgreSQL tool or a part of the new stack:

| JADECare Start application(s) | PostgreSQL or stack equivalent |
|---|---|
| `KCCheckConnection` | `GET /healthz` (section 5); `pg_isready` |
| `KCDatabaseBackup`, `KCDatabaseBackupMulti`, `KCDbBackupWorker` | `pg_dump -Fc` (logical backup), restored with `pg_restore` |
| `KCOnLineBackup`, `KCOnLineBackupWorker` | `pg_basebackup` with WAL archiving (`archive_mode`, `archive_command`); `pg_verifybackup` |
| `KCOnLineCompact`, `KCOnLineCompactWorker`, `KCOnLinePartitionWorker` | Autovacuum; `VACUUM (ANALYZE)` / `vacuumdb`; `REINDEX` / `reindexdb` |
| `KCVerifyJournal` | `pg_waldump` (WAL inspection); `pg_amcheck` and `pg_checksums` (integrity) |
| `KCNodeControl`, `KCStartAppInNode`, `KCCMgr` | `pg_ctl start\|stop\|status`; the operating system's service manager for the API process |
| `KCLockCheck` | `pg_locks` joined to `pg_stat_activity` (section 7) |
| `KCStatsCollector`, `KCStartMonitorBackground`, `KCSDSChecks` | The `pg_stat_*` views (`pg_stat_replication` where replication is used); the Serilog output (section 6) |
| `KCSchemaCopy`, `KCSchemaCopyAppServer`, `KCDeployMgr` | EF Core migrations (the `migrate` verb, `dotnet ef migrations script`, section 4.3); `pg_dump --schema-only` |
| `KCBatch` | The operating system's scheduler (cron or systemd timers) |
| `KCTodaysDate` | `SELECT CURRENT_DATE`; the store's server date is the date of `clock_timestamp()::timestamp(3)` on the database server, in its local time (D-15) |
| `KCGui`, `KCGuiTimer`, `KCDummy`, `KCValidateMethods` | No equivalent needed; `dotnet build` compiles and checks the code |

Example commands:

```bash
pg_isready -h <host> -p <port>
pg_dump -Fc -f "<file>.dump" <database>
pg_restore -d <database> "<file>.dump"
pg_dump --schema-only -f "<file>.sql" <database>
pg_basebackup -D "<backup dir>" -X stream -P -c fast
pg_verifybackup "<backup dir>"
vacuumdb --analyze <database>
reindexdb <database>
pg_amcheck <database>
pg_waldump "<data dir>/pg_wal/<WAL segment file>"
pg_checksums --check -D "<data dir>"
pg_ctl status -D "<data dir>"
psql -d <database> -c 'SELECT CURRENT_DATE, clock_timestamp()::timestamp(3);'
```

Requirements of these tools:

- `pg_basebackup` needs a role with the `REPLICATION` attribute, or a superuser, and a matching `replication` entry in `pg_hba.conf`. `-c fast` starts the backup with an immediate checkpoint.
- `pg_verifybackup` checks a plain-format backup against its `backup_manifest`.
- `vacuumdb` run as the database owner skips the shared catalogs with a warning; a superuser processes them too.
- `pg_amcheck` needs the `amcheck` extension in the database; `pg_amcheck --install-missing` creates it, which needs a superuser.
- `pg_checksums --check` needs a cleanly stopped cluster and a cluster initialised with data checksums (`initdb --data-checksums`, or `pg_checksums --enable` on a stopped cluster).
- `pg_ctl` runs as the operating system user that owns the data directory, not as root.

The target defines no replication; the JADE replica and SDS checks have no counterpart.

## 9. Not provided

No container image, installer, CI/CD pipeline or environment-specific configuration is provided. Production deployment is outside this work.
