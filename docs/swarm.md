# Swarm Integration Guide

How to add the Swarmwright swarm-orchestration system to a .NET application, configure its LLM
backend and database, and expose the REST + SSE API. This is the "start here" document; the
template authoring guide ([template.md](template.md)), the recovery model
([resilience.md](resilience.md)), and the state machine
([state-machine.md](state-machine.md)) go deeper on individual subsystems.

---

## 1. Adding Swarm to Your Application

### Packages

- `Swarmwright` — the orchestration engine, state machine, templates, and the hosted dispatcher.
- `Swarmwright.AspNetCore` — the HTTP surface: the REST + SSE endpoints, the `Swarm.Read` /
  `Swarm.Write` authorization policies, Entra ID authentication wiring, and the `/api/spa-config`
  endpoint the admin SPA reads.
- `Swarmwright.MicrosoftAgentFramework` — the `IChatClient` registration (Azure OpenAI or any
  OpenAI-compatible endpoint).
- A database package: `Swarmwright.Database.Postgres` or `Swarmwright.Database.Sqlite` (the
  in-memory provider is built in for tests and local spikes).

### Program.cs (Azure OpenAI, one call)

The simplest wiring registers the whole stack from the `AzureOpenAI` configuration section and maps
the endpoints:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Service registration: LLM client, database, templates, hosted dispatcher, auth policies.
builder.Services.AddAISwarm(builder.Configuration, builder.Environment);

var app = builder.Build();

// REST + SSE endpoint mapping under /api/swarm.
app.MapSwarmEndpoints(useSwarmPolicies: true);

app.Run();
```

`AddAISwarm` (in
[`../src/Swarmwright.AspNetCore/Extensions/IServiceCollectionExtensions.cs`](../src/Swarmwright.AspNetCore/Extensions/IServiceCollectionExtensions.cs))
is a convenience method that calls four registrations and, by default, scans loaded assemblies for
custom tool providers:

| Method | Registers |
|--------|-----------|
| `AddSwarmOrchestration` | The shared `IChatClient` (Azure OpenAI, wrapped for resilience and automatic function invocation) from the `AzureOpenAI` section |
| `AddSwarmDomain` | Database, repositories, template loader, inbox, team registry, event bus, state machine, and the hosted dispatcher |
| `AddSwarmHttpServices` | The scoped services behind the HTTP surface (the refinement chat handler) |
| `AddSwarmAuthorization` | The `Swarm.Read` and `Swarm.Write` authorization policies |

Pass `discoverCustomToolProviders: false` to `AddAISwarm` to skip the automatic scan and register
`ICustomToolProvider` implementations yourself. Set `useSwarmPolicies: false` on
`MapSwarmEndpoints` to leave the endpoints anonymous during development (the parameter defaults to
`false`).

### Program.cs (OpenAI-compatible endpoint, e.g. vLLM or Ollama)

`AddAISwarm` binds Azure OpenAI. To talk to a local OpenAI-compatible server instead, register the
`IChatClient` explicitly with `AddSwarmwrightOpenAI` and then call the same domain / HTTP / auth
helpers directly:

```csharp
builder.Services.AddSwarmwrightOpenAI(
    endpoint: builder.Configuration["OpenAI:Endpoint"]!,   // e.g. http://localhost:8000/v1
    model: builder.Configuration["OpenAI:Model"] ?? "Qwen/Qwen2.5-7B-Instruct",
    apiKey: builder.Configuration["OpenAI:ApiKey"]);        // many local servers ignore this
builder.Services.AddSwarmDomain(builder.Configuration, builder.Environment);
builder.Services.AddSwarmHttpServices();
builder.Services.AddSwarmAuthorization();
```

Both LLM registrations are idempotent (`TryAddSingleton`), so whichever registers the `IChatClient`
first wins. The example host
([`../tests/Swarmwright.Example.WebHost/Program.cs`](../tests/Swarmwright.Example.WebHost/Program.cs))
uses exactly this pattern: it registers the OpenAI-compatible client when `OpenAI:Endpoint` is set
and otherwise falls back to `AddAISwarm` (Azure OpenAI).

---

## 2. Configuration

Three top-level sections drive the swarm: the LLM backend (`AzureOpenAI` **or** `OpenAI`), the
`Swarm` section (database, work directories, concurrency), and — when authentication is enabled —
the `AzureAd` and `SpaConfiguration` sections (see [§6](#6-authentication--authorization)).

### LLM backend

`AddAISwarm` reads the top-level **`AzureOpenAI`** section
([`AzureOpenAIOptions`](../src/Swarmwright.MicrosoftAgentFramework/Configuration/AzureOpenAIOptions.cs)):

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "ApiKey": "OVERRIDE_VIA_USER_SECRETS",
    "DeploymentName": "gpt-4o"
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `AzureOpenAI:Endpoint` | (required) | Azure OpenAI resource endpoint |
| `AzureOpenAI:ApiKey` | (required) | API key (use user-secrets or env vars) |
| `AzureOpenAI:DeploymentName` | (required) | Model deployment name; registration throws if empty |
| `AzureOpenAI:NetworkTimeoutSeconds` | `600` | Per-request SDK network timeout |
| `AzureOpenAI:MaxLlmRetries` | `6` | SDK-layer retry count (`ClientRetryPolicy`) |
| `AzureOpenAI:MaxPollyRetries` | `3` | Polly-layer 429 retry count |
| `AzureOpenAI:RetryBaseDelaySeconds` | `2.0` | Polly exponential-backoff base delay |
| `AzureOpenAI:UseBackgroundResponses` | `false` | Wire the client onto the OpenAI Responses API (background runs + resume) |

The OpenAI-compatible path reads a top-level **`OpenAI`** section instead:

```json
{
  "OpenAI": {
    "Endpoint": "http://localhost:8000/v1",
    "Model": "Qwen/Qwen2.5-7B-Instruct",
    "ApiKey": ""
  }
}
```

### Swarm section

Everything else lives under the `Swarm` section
([`SwarmOptions`](../src/Swarmwright/Configuration/SwarmOptions.cs)):

```json
{
  "Swarm": {
    "TemplatesDirectory": "templates",
    "WorkBasePath": "./swarm-workdirs",
    "Database": {
      "Provider": "PostgreSQL",
      "ConnectionString": "Host=localhost;Port=5432;..."
    },
    "MaxRounds": 8,
    "SuspendTimeoutSeconds": 1800,
    "MaxConcurrentSwarms": 4,
    "MaxQueuedSwarms": 10
  }
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Swarm:TemplatesDirectory` | `templates` | Templates root; a relative path resolves against `AppContext.BaseDirectory` |
| `Swarm:WorkBasePath` | `{TEMP}/swarm-work` | Base path for per-swarm work directories |
| `Swarm:Database:Provider` | `InMemory` | `PostgreSQL`, `SQLite`, or `InMemory` |
| `Swarm:Database:ConnectionString` | `Data Source=swarm.db` | Connection string for the chosen provider |
| `Swarm:MaxRounds` | `8` | Maximum execution rounds before the orchestrator stops |
| `Swarm:SuspendTimeoutSeconds` | `1800` | Time to wait before auto-suspending a blocked swarm |
| `Swarm:MaxConcurrentSwarms` | `4` | Concurrent swarm executions (semaphore limit) |
| `Swarm:MaxQueuedSwarms` | `10` | Bounded channel capacity; backpressure when full |
| `Swarm:MaxTaskRetries` | `1` | Continue-driven retries allowed per failed task |
| `Swarm:AutoSmartContinueAttempts` | `0` | Inline auto Smart-Continue attempts before escalating to `NeedsDiagnosis` (`0` disables) |
| `Swarm:DiagnoseLockTimeoutMinutes` | `30` | Stale-timeout after which a diagnose lock may be stolen without confirmation |

Swarm-run archival is configured under `Swarm:Archival` — see
[archival.md](archival.md).

### Sensitive values

Store the API key and connection string in user-secrets or environment variables rather than
`appsettings.json`:

```bash
dotnet user-secrets set "AzureOpenAI:ApiKey" "your-key-here"
dotnet user-secrets set "Swarm:Database:ConnectionString" "Host=..."
```

Environment variables use `__` as the section separator: `AzureOpenAI__ApiKey`,
`Swarm__Database__ConnectionString`. The example host ships a
[`../scripts/set-user-secrets.ps1`](../scripts/set-user-secrets.ps1) helper that seeds these from a
local `.env` file.

---

## 3. Work Directories

Each swarm execution gets an isolated work directory for its file artifacts:

```
{WorkBasePath}/{swarmId}/
```

- If `Swarm:WorkBasePath` is not set, it defaults to `{TEMP}/swarm-work/`.
- The directory is created on demand for the swarm.
- Workers use `read` and `write` tools scoped to this directory (see
  [`../src/Swarmwright/Tools/DefaultToolFactory.cs`](../src/Swarmwright/Tools/DefaultToolFactory.cs)).
- All paths are validated by `PathSecurity`
  ([`../src/Swarmwright/Tools/PathSecurity.cs`](../src/Swarmwright/Tools/PathSecurity.cs)), which
  rejects `..` traversal, absolute paths, and escapes outside the work directory.
- Artifacts are exposed via `GET /api/swarm/{id}/artifacts`,
  `GET /api/swarm/{id}/artifacts/{path}`, and `GET /api/swarm/{id}/artifacts/download-zip`.
- The synthesis phase writes its report to `synthesis-report.md` in the work directory.

---

## 4. LLM Backend and Model Assignment

The swarm shares a single `IChatClient` across every agent — leader and workers alike. Two backends
are supported, both registered in
[`../src/Swarmwright.MicrosoftAgentFramework/Extensions/ServiceCollectionExtensions.cs`](../src/Swarmwright.MicrosoftAgentFramework/Extensions/ServiceCollectionExtensions.cs):

- **Azure OpenAI** (`AddSwarmwrightAzureOpenAI`, used by `AddAISwarm`) — reads the `AzureOpenAI`
  section and invokes `AzureOpenAI:DeploymentName`.
- **Any OpenAI-compatible endpoint** (`AddSwarmwrightOpenAI`) — vLLM, Ollama, LM Studio, or OpenAI
  itself, addressed by its served model name.

In both cases the client is built with automatic function (tool-call) invocation and wrapped in a
resilience layer (`ResilientChatClient`) that applies Polly retries for throttling. To change the
model, update `AzureOpenAI:DeploymentName` (Azure) or `OpenAI:Model` (OpenAI-compatible).

For Azure, common deployment choices:

| Model | Use Case |
|-------|----------|
| `gpt-4o` | Multimodal, fast, good for general tasks |
| `gpt-4.1` | Higher quality for complex reasoning, slower |
| `gpt-4.1-mini` | Cheaper and faster for lighter templates |

---

## 5. HTTP API Endpoints

`MapSwarmEndpoints` maps everything under `/api/swarm`
([`../src/Swarmwright.AspNetCore/Extensions/SwarmEndpointExtensions.cs`](../src/Swarmwright.AspNetCore/Extensions/SwarmEndpointExtensions.cs)).
Route IDs are GUID-constrained (`{id:guid}`).

### Write operations (`Swarm.Write` policy)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/swarm/` | Create swarm. Body: `{ "goal": "...", "templateKey": "...", "context": { "k": "v" } }` (`context` optional — free-form metadata exposed to custom tools via `ISwarmRunContext`). Returns 201 with `{ swarmId }`. `goal` is required and capped at 16,384 chars; `templateKey` must match `^[A-Za-z0-9_-]+$`. |
| `POST` | `/{id}/cancel` | Cancel a running swarm. |
| `POST` | `/{id}/continue` | Deterministic resume. Accepted when at least one Failed task has retry budget OR at least one task is Pending. |
| `POST` | `/{id}/smart-continue` | Leader-driven repair. Invokes the leader advisor with the current failed tasks to produce a reset/add/abandon plan. Short-circuits to plain Executing when there are zero failed tasks but viable open work. |
| `POST` | `/{id}/skip` | Force Synthesis — jump straight to the synthesis phase and produce a report from Completed work. |
| `POST` | `/{id}/lock` | Acquire the diagnose lock (optional `?steal=true` to take a stale lock). |
| `DELETE` | `/{id}/lock` | Release the diagnose lock. |
| `POST` | `/{id}/mark-as-awaiting-intervention` | Flip a Failed swarm into `AwaitingIntervention` so an operator can pick a recovery action. |
| `POST` | `/{id}/copilot` | Refinement chat — a conversational handler over the swarm's work directory. |

See [resilience.md § 2](resilience.md#2-recovery-resilience-the-recommendation-surface)
for the mental model behind each recovery action and when to pick which.

### Read operations (`Swarm.Read` policy)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/swarm/` | List swarms (in-memory + persisted, merged). Query: `limit` (default 50, max 500), `since`. |
| `GET` | `/{id}` | Swarm metadata (`SwarmMetadataResponse`): goal, template, phase, state, isRunning, timestamps, lock info, plus a `recommendation` object when the swarm is in an actionable non-terminal state. |
| `GET` | `/{id}/tasks` | List tasks (status, worker, result). |
| `GET` | `/{id}/agents` | List agents. |
| `GET` | `/{id}/messages` | List agent messages. |
| `GET` | `/{id}/events` | Event log. Query: `limit` (default 100). |
| `GET` | `/{id}/stream` | SSE event stream (AG-UI protocol, 15s heartbeat). |
| `GET` | `/templates` | List available templates (key, name, description). |
| `GET` | `/{id}/artifacts` | List work-directory files. |
| `GET` | `/{id}/artifacts/{path}` | Download a single artifact (path-validated). |
| `GET` | `/{id}/artifacts/download-zip` | Download the whole work directory as a ZIP. |

Responses are serialized with the library's own JSON options (camelCase, PascalCase enum names) so
the shape is stable regardless of the host's framework-level JSON configuration.

---

## 6. Authentication & Authorization

### Authorization policies

`AddSwarmAuthorization`
([`../src/Swarmwright.AspNetCore/Extensions/SwarmAuthorizationExtensions.cs`](../src/Swarmwright.AspNetCore/Extensions/SwarmAuthorizationExtensions.cs))
registers two policies bound to the `Bearer` scheme:

- **`Swarm.Read`** — requires the `Swarm.Read` scope or the `Swarm.Admin` app role.
- **`Swarm.Write`** — requires the `Swarm.Write` scope or the `Swarm.Admin` app role.

Both accept either the space-delimited `scp` claim or the
`http://schemas.microsoft.com/identity/claims/scope` claim. The `Swarm.Admin` role covers
machine-to-machine callers. Set `useSwarmPolicies: false` on `MapSwarmEndpoints` to disable the
policies during development.

### Authentication (Entra ID)

Bearer-token validation is opt-in and separate from the policies. When you want authenticated
access, wire Microsoft.Identity.Web with `AddSwarmAzureAdAuthentication` and expose the anonymous
SPA config endpoint with `AddSwarmSpaConfiguration` + `MapSwarmSpaConfig`
([`../src/Swarmwright.AspNetCore/Extensions/SwarmAuthenticationExtensions.cs`](../src/Swarmwright.AspNetCore/Extensions/SwarmAuthenticationExtensions.cs)):

```csharp
var authEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]);
if (authEnabled)
{
    builder.Services.AddSwarmAzureAdAuthentication(builder.Configuration);
    builder.Services.AddSwarmSpaConfiguration(builder.Configuration);
}

// ... after builder.Build():
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapSwarmSpaConfig();   // GET /api/spa-config — anonymous, feeds MSAL.js
}
app.MapSwarmEndpoints(useSwarmPolicies: authEnabled);
```

Two configuration sections drive this:

- **`AzureAd`** (`Instance`, `TenantId`, `ClientId`, `Audience`) — the Microsoft.Identity.Web
  settings used to validate incoming bearer tokens.
- **`SpaConfiguration`** (`ClientId`, `TenantId`, `DefaultScope`, `RequiredPermissions`) — returned
  from `GET /api/spa-config` so the admin SPA can configure MSAL.js without baking secrets into its
  bundle.

Leaving the `AzureAd` section empty keeps the host anonymous, so a bare `dotnet run` still works for
local spikes. See [azure-ad-setup.md](azure-ad-setup.md) for the app-registration
walkthrough; the [`../scripts/provision-app-registration.ps1`](../scripts/provision-app-registration.ps1)
script provisions the registration and its scopes/roles. The admin SPA itself is covered by
[admin.md](admin.md). The MCP server has its own `SwarmMcp.Read` / `SwarmMcp.Write`
authorization surface — see [mcp-server.md](mcp-server.md).

---

## 7. Database Providers

The swarm persists swarm state, tasks, agents, messages, events, and file metadata. Three providers
are supported (`Swarm:Database:Provider`):

| Provider | Config value | Migrations assembly | Use case |
|----------|--------------|---------------------|----------|
| InMemory | `"InMemory"` | (none) | Tests, development. Data lost on restart |
| SQLite | `"SQLite"` | `Swarmwright.Database.Sqlite` | Single-instance local development |
| PostgreSQL | `"PostgreSQL"` | `Swarmwright.Database.Postgres` | Production, multi-instance |

The context is registered as an `IDbContextFactory<SwarmDbContext>`, so every repository call gets a
fresh short-lived context and concurrent swarm workers never share a `DbContext`. In the
`Development` and `Testing` environments, `SwarmMigrationRunner` (a hosted service) auto-applies EF
Core migrations at startup.

---

## 8. Concurrency Model

The hosted `SwarmDispatcherService` manages swarm execution:

- **Bounded channel** — create requests queue up to `Swarm:MaxQueuedSwarms` before backpressure.
- **Semaphore** — concurrent executions are limited to `Swarm:MaxConcurrentSwarms`.
- **Per-swarm DI scope** — each swarm runs in its own service scope (inbox, team registry, run
  context, intervention handler).
- **CancellationToken chaining** — host shutdown cancels all running swarms gracefully.
- **Eviction window** — a finished `SwarmExecution` is held in memory for roughly 5 minutes after
  its run returns; reads past that window fall back to the database, and an evicted swarm is
  transparently re-hydrated when a `/stream` or recovery request arrives.

---

## 9. Complete Example

### appsettings.json

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://my-resource.openai.azure.com/",
    "ApiKey": "OVERRIDE_VIA_USER_SECRETS",
    "DeploymentName": "gpt-4o"
  },
  "Swarm": {
    "TemplatesDirectory": "templates",
    "WorkBasePath": "./swarm-work",
    "Database": {
      "Provider": "PostgreSQL",
      "ConnectionString": "OVERRIDE_VIA_USER_SECRETS"
    },
    "MaxRounds": 8,
    "MaxConcurrentSwarms": 4
  }
}
```

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAISwarm(builder.Configuration, builder.Environment);

var app = builder.Build();

app.MapSwarmEndpoints(useSwarmPolicies: true);

app.Run();
```

### Creating a swarm

```bash
curl -X POST http://localhost:5000/api/swarm \
  -H "Content-Type: application/json" \
  -d '{"goal": "Research the impact of AI on healthcare", "templateKey": "deep-research"}'
```

### Checking status

```bash
curl http://localhost:5000/api/swarm/{swarmId}
curl http://localhost:5000/api/swarm/{swarmId}/tasks
curl http://localhost:5000/api/swarm/{swarmId}/artifacts
```
