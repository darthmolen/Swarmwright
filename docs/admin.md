# Swarm Admin UI

The Swarm Admin UI is a React single-page application (`swarmwright-admin`) that provides a
real-time management surface for AI swarm orchestration. It is a standalone Vite app living at
[`../src/swarmwright-admin`](../src/swarmwright-admin); the example web host builds it and serves the
compiled static assets from its own `wwwroot`, so the swarm can be driven from a browser without a
separate frontend deployment.

The SPA talks to the swarm REST + SSE endpoints registered by `MapSwarmEndpoints()` (in
`Swarmwright.AspNetCore`) and fetches its authentication configuration at startup from the anonymous
`GET /api/spa-config` endpoint, so a single build artifact targets any environment — only the host's
configuration changes.

## How It Is Served

The reference [`tests/Swarmwright.Example.WebHost`](../tests/Swarmwright.Example.WebHost) host shows
the full wiring. There is no NuGet UI package and no dedicated base path — the SPA is served as
static files at the **site root (`/`)**.

### 1. Register services

`Program.cs` wires the swarm API, and — only when Entra ID (Azure AD) configuration is present —
adds authentication plus the SPA configuration options:

```csharp
using Swarmwright.Extensions;

builder.Services.AddSwarmwright(builder.Configuration, builder.Environment);

var authEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]);
if (authEnabled)
{
    builder.Services.AddSwarmAzureAdAuthentication(builder.Configuration);
    builder.Services.AddSwarmSpaConfiguration(builder.Configuration);
}
```

When the `AzureAd` section is empty the host stays anonymous, so a bare `dotnet run` still works for
local spikes.

### 2. Map static files and endpoints

After building the app, serve the static SPA assets, map the anonymous SPA-config endpoint (when
auth is enabled), map the swarm REST + SSE API, and fall back to `index.html` for client-side
routes:

```csharp
app.UseStaticFiles();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    // Anonymous config endpoint the SPA fetches before login to configure MSAL.
    app.MapSwarmSpaConfig();
}

app.MapSwarmEndpoints(useSwarmPolicies: authEnabled);
app.MapFallbackToFile("index.html");
```

`MapSwarmEndpoints(useSwarmPolicies: true)` applies the `Swarm.Read` / `Swarm.Write` policies to the
API; when `false`, the endpoints are anonymous. `MapSwarmSpaConfig()` (defined in
[`SwarmAdminEndpointExtensions.cs`](../src/Swarmwright.AspNetCore/Extensions/SwarmAdminEndpointExtensions.cs))
maps the single `GET /api/spa-config` route the SPA reads before login.

### 3. Configuration

Add a `SpaConfiguration` section — bound to the `SpaConfiguration` options — with your Entra ID app
registration details:

```json
{
  "SpaConfiguration": {
    "ClientId": "<your-spa-client-id>",
    "TenantId": "<your-azure-ad-tenant-id>",
    "DefaultScope": "api://<your-api-app-id>/.default",
    "RequiredPermissions": [
      "api://<your-api-app-id>/Swarm.Read",
      "api://<your-api-app-id>/Swarm.Write"
    ]
  }
}
```

The API itself validates bearer tokens using the standard `AzureAd` section (Microsoft.Identity.Web).
Real secrets belong in user-secrets or Key Vault, not in `appsettings.json` — the
[`scripts/set-user-secrets.ps1`](../scripts/set-user-secrets.ps1) helper seeds them locally and
[`scripts/provision-app-registration.ps1`](../scripts/provision-app-registration.ps1) creates the
underlying registrations.

`GET /api/spa-config` is anonymous and returns `clientId`, `tenantId`, `defaultScope`, and
`requiredPermissions`; the SPA fetches it, builds its MSAL configuration, and requests those scopes
during login. Because the config is served (not baked into the bundle), the same static build works
across every environment.

For step-by-step instructions on creating the Entra ID app registrations referenced above (the SPA's
own `ClientId` plus the API app it calls into), see [azure-ad-setup.md](azure-ad-setup.md).

## Building the SPA

The admin SPA is built with Vite and requires **Node.js at build time**. The example host's
`.csproj` runs the build as an MSBuild target: it runs `npm ci` (when `node_modules` is missing) and
`npm run build`, then copies the Vite `dist/` output into `wwwroot`, which the ASP.NET Web SDK serves
automatically. The step is incremental — npm only re-runs when SPA sources change.

Environments without Node.js (C#-only builds, restricted CI) can skip the SPA build entirely:

```bash
dotnet build -p:SkipSpaBuild=true
```

The host then serves whatever assets already exist in `wwwroot` (or none, in which case only the
JSON/SSE API is available).

### Local development

For live editing with hot-module reload, run the Vite dev server directly instead of rebuilding the
host:

```bash
cd src/swarmwright-admin
npm run dev
```

The dev server listens on **`http://localhost:5173`** and proxies `/api/*` calls to the example web
host at **`https://localhost:7001`** (its Kestrel HTTPS port). Run the web host separately so the
proxied API and `/api/spa-config` are available.

## UI Overview

The Swarm Admin provides a real-time management interface for AI swarm orchestration. It connects to
the host's swarm endpoints via Server-Sent Events (SSE) for live updates.

### Authentication

When the host is configured for Entra ID, the SPA fetches `/api/spa-config` on first load and
redirects to Entra ID login via MSAL. Users need the scopes defined in `RequiredPermissions`
(typically `Swarm.Read` and `Swarm.Write`). When the host runs anonymously, the SPA operates without
a sign-in step.

### Dashboard View

The main view shown while a swarm is running or when browsing session history.

| Panel | Purpose |
|-------|---------|
| **Swarm Controls** | Enter a goal, select a template, and start a swarm. Also offers a template-pack ZIP upload affordance. |
| **Swarm Status** | Live phase indicator (planning, executing, synthesizing, etc.), round counter, and progress bar. Includes the recovery/intervention controls. |
| **Agent Roster** | Grid of agent cards showing each worker's role, status, tasks completed, and last output snippet. |
| **Task Board** | Kanban board with columns for blocked, pending, in-progress, completed, failed, and timed-out tasks. Click a task for full details. |
| **Inbox Feed** | Chronological feed of inter-agent messages showing sender, recipient, and content. |
| **Active Tools** | Live tool-call cards showing which tools agents are invoking, their arguments, and results. |
| **Session History** | Left sidebar listing past swarm sessions, filterable and searchable. |

### Report View

After a swarm completes, the report view shows:

- **Artifact List** — generated files (markdown reports, synthesis output). Download individual files
  or a ZIP of all artifacts.
- **Report Content** — rendered markdown with Mermaid diagram support.
- **Task Pill Bar** — compact overview of all tasks with a click-to-expand detail drawer.
- **Refinement Chat** — a chat panel for follow-up questions to the synthesis agent.

### Intervention View

When a swarm suspends (for example, a worker fails), the intervention view lets operators pick a
recovery strategy. Four buttons are always visible (capability parity); the server-computed
recommendation surfaces as a highlight + tooltip on the recommended button:

- **Continue** — deterministic resume. Retries Failed-with-budget tasks and/or picks up viable
  Pending work. Does not re-plan.
- **Smart Continue** — leader-driven repair. The leader LLM reviews the board and produces a
  reset/add/abandon plan.
- **Force Synthesis** — abandon remaining open work and produce a report from whatever Completed
  tasks exist.
- **Cancel** — terminate the swarm; no synthesis.

The SPA reads the `recommendation` object from `GET /api/swarm/{id}`:

```json
"recommendation": {
  "validActions": ["continue", "smart-continue", "force-synthesis", "cancel"],
  "recommendedAction": "continue",
  "rationale": "No failures. 1 Pending task(s) viable. Continue resumes the workflow."
}
```

The `rationale` string renders as a tooltip on the highlighted button so the operator sees *why* the
server suggests that action. See [resilience.md](resilience.md) for the rule table behind
the recommendation.

Additional intervention affordances:

- **Edit worker templates** — a YAML template editor for adjusting worker definitions.
- **Review logs** — agent output with error highlighting.
- **Provide guidance** — chat with the orchestrator to guide a retry.

### API Endpoints Used

The SPA communicates with the host via the endpoints below. Except for `/api/spa-config` (mapped
separately by `MapSwarmSpaConfig()`), all are registered under the `/api/swarm` group by
`MapSwarmEndpoints()`. Write endpoints require the `Swarm.Write` policy and read endpoints the
`Swarm.Read` policy when the host is started with `useSwarmPolicies: true`.

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/swarm` | Create a new swarm |
| GET | `/api/swarm` | List swarms (active + history) |
| GET | `/api/swarm/{id}` | Get swarm state (includes the `recommendation` object) |
| GET | `/api/swarm/{id}/stream` | SSE event stream |
| GET | `/api/swarm/{id}/tasks` | List tasks |
| GET | `/api/swarm/{id}/agents` | List agents |
| GET | `/api/swarm/{id}/messages` | List inter-agent messages |
| GET | `/api/swarm/{id}/events` | List recorded events |
| POST | `/api/swarm/{id}/copilot` | Refinement chat (CopilotKit runtime) |
| POST | `/api/swarm/{id}/continue` | Deterministic resume (retries or picks up Pending) |
| POST | `/api/swarm/{id}/smart-continue` | Leader-driven repair via `repair_plan_after_failure` |
| POST | `/api/swarm/{id}/skip` | Force Synthesis |
| POST | `/api/swarm/{id}/cancel` | Cancel the swarm |
| POST | `/api/swarm/{id}/lock` | Acquire an intervention lock |
| DELETE | `/api/swarm/{id}/lock` | Release the intervention lock |
| POST | `/api/swarm/{id}/mark-as-awaiting-intervention` | Flag the swarm as awaiting operator intervention |
| GET | `/api/swarm/templates` | List available templates |
| GET | `/api/swarm/{id}/artifacts` | List output files |
| GET | `/api/swarm/{id}/artifacts/download-zip` | Download all artifacts as a ZIP |
| GET | `/api/swarm/{id}/artifacts/{**path}` | Download a single artifact |
| GET | `/api/spa-config` | SPA auth configuration (anonymous) |
