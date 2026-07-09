# Swarm MCP Server

The Swarm MCP Server exposes the swarm's operational surface (create, monitor, inspect, intervene, cancel) as MCP tools over HTTP so other AI agents can drive swarms as if they were first-class tool calls. It ships as the `Swarmwright.McpServer` project and plugs into any ASP.NET host via two extension methods.

Tool names are `snake_case` so agents wired to an equivalent Python reference MCP server can point at the C# host with no prompt changes.

## When to Use This

Use the Swarm MCP Server when:

- You want an AI agent (running in your host or elsewhere) to launch and supervise swarms by calling MCP tools instead of your REST API.
- You need a structured, typed contract for swarm operations that tool-calling models can discover via `tools/list`.
- You already have swarm orchestration wired up via `AddSwarmwright(...)` and want to expose it to agents without building a custom adapter.

If you only need a human UI, use [Swarm Admin](admin.md) instead — that is the React SPA.

## Adding to Your Application

The MCP server is a separate project and is **not** wired into the example host by default; you add it to your own host with the two calls below.

### 1. Add the Project Reference

The server lives at `src/Swarmwright.McpServer`. Reference it from your host's `.csproj`:

```xml
<ProjectReference Include="..\..\src\Swarmwright.McpServer\Swarmwright.McpServer.csproj" />
```

It depends on `Swarmwright`, which you should already reference if you are calling `AddSwarmwright`.

### 2. Register Services

After `AddSwarmwright`, register the MCP server:

```csharp
using Swarmwright.McpServer.Extensions;

builder.Services.AddSwarmwright(builder.Configuration, builder.Environment);
builder.Services.AddSwarmMcpServer(builder.Configuration);
```

`AddSwarmMcpServer(IConfiguration)` binds the `SwarmMcp` and `SwarmMcpAuthorization` sections, registers the authentication scheme for the configured `SwarmMcp:AuthMode`, registers the `SwarmMcp.Read` and `SwarmMcp.Write` authorization policies, and registers the `SwarmMcpTools` type — the single tool class that carries all 18 tools — on an `AddMcpServer().WithHttpTransport()` server.

### 3. Map the Endpoint

After building the app, map the MCP endpoint:

```csharp
app.UseAuthentication();
app.UseAuthorization();

app.MapSwarmEndpoints();       // swarm REST + SSE (you likely already have this)
app.MapSwarmMcpServer();       // streamable-HTTP MCP at the configured path (default /swarm/mcp)
```

`MapSwarmMcpServer` maps the configured endpoint path (default `/swarm/mcp`) with `.RequireAuthorization("SwarmMcp.Read")`. When `AuthMode=None` it logs a startup warning that all callers are granted Read+Write. See [Authorization](#authorization) for how the Write policy fits in.

## Configuration

Add these sections to your `appsettings.json`:

```json
{
  "SwarmMcp": {
    "AuthMode": "None",
    "EndpointPath": "/swarm/mcp",
    "ApiKey": "",
    "MaxArtifactBytes": 262144
  },
  "SwarmMcpAuthorization": {
    "ReadRole": "SwarmMcp.Read",
    "WriteRole": "SwarmMcp.Write",
    "ReadScope": "SwarmMcp.Read",
    "WriteScope": "SwarmMcp.Write"
  }
}
```

### `SwarmMcp` section

| Key | Type | Description |
|---|---|---|
| `AuthMode` | `None` \| `ApiKey` \| `AzureAD` | How inbound callers authenticate. See [Authentication](#authentication) below. Default `None`. |
| `EndpointPath` | string | HTTP path at which the MCP endpoint is mapped. Default `/swarm/mcp`. |
| `ApiKey` | string | Expected API key when `AuthMode=ApiKey`. Put the real value in user-secrets, not in `appsettings.json`. |
| `MaxArtifactBytes` | int | Max bytes returned by `read_artifact` before truncation. Default 262144 (256 KiB). |

### `SwarmMcpAuthorization` section

Role and scope names the policies accept. You only need to override these if your Entra ID (Azure AD) app registration uses different names than the defaults. All four default to their policy name (`SwarmMcp.Read` / `SwarmMcp.Write`).

## Authentication

Three modes, selectable via `SwarmMcp:AuthMode`:

### `None`
No auth. The `NoAuthenticationHandler` unconditionally succeeds and fabricates both `SwarmMcp.Read` and `SwarmMcp.Write` role claims. A startup warning is logged. **Dev only.**

### `ApiKey`
Expects an `X-API-Key` header on every request. The value is checked against `SwarmMcp:ApiKey`. On success the handler fabricates both Read and Write role claims so the downstream authorization policies succeed. Failure (missing header, mismatch, or unconfigured expected key) fails authentication.

### `AzureAD`
Azure AD JWT Bearer validation via `Microsoft.Identity.Web` (`AddMicrosoftIdentityWebApi`). Uses the host's existing `AzureAd` configuration section. Callers must present a token with either:

- App-to-app: the `roles` claim containing `SwarmMcp.Read` (or `SwarmMcp.Write`).
- Delegated: the `scp` claim containing `SwarmMcp.Read` (or `SwarmMcp.Write`).

The `SwarmMcpAuthorizationHandler` requirement is satisfied by the **role or the scope**, so both app and user flows work with the same registration.

For step-by-step instructions on provisioning the Entra ID app registrations that publish these scopes and roles, see [azure-ad-setup.md](azure-ad-setup.md).

### Coexisting with other schemes

`AddSwarmMcpServer` does **not** change the host's default authentication scheme. Each Swarm MCP policy is pinned to the specific scheme it needs via `policy.AuthenticationSchemes.Add(...)`. Other endpoints (for example your REST API sitting behind AAD Bearer) keep their existing auth intact when the swarm MCP is added.

## Authorization

The single MCP endpoint is gated by the **`SwarmMcp.Read`** policy (`MapSwarmMcpServer` calls `.RequireAuthorization("SwarmMcp.Read")`). Both the `SwarmMcp.Read` and `SwarmMcp.Write` policies are registered, each pinned to the authentication scheme appropriate for the chosen `AuthMode`.

- In `None` and `ApiKey` modes, the handlers grant **both** Read and Write claims, so every tool — read and write alike — is reachable once the caller passes the Read gate.
- In `AzureAD` mode, the endpoint requires a token satisfying the Read role or scope. The `SwarmMcp.Write` policy is registered with the same role/scope names (`SwarmMcp.Write`) for hosts that want to add write-level checks; publish the Write role/scope on your app registration so write-capable callers can be distinguished.

## Tool Catalog

All 18 tools are registered on the single endpoint via the `SwarmMcpTools` class. Tool names use `snake_case`. The catalog is grouped by category so consumers can find what they need fast.

### Inspect (read)

| Tool | Purpose |
|---|---|
| `get_active_swarms()` | List swarms the in-memory manager is currently tracking. |
| `get_swarm_status(swarmId)` | Phase, running flag, agent count, task counts grouped by status. |
| `get_swarm_summary(swarmId)` | Token-efficient, phase-aware digest suitable for quick orientation. **Includes the `recommendation` object** when the swarm is in an actionable non-terminal state (`AwaitingIntervention` / `NeedsDiagnosis`). |
| `get_swarm_recommendation(swarmId)` | Returns just the recovery recommendation: `validActions`, `recommendedAction`, `rationale`. `null` for swarms not in an actionable state. See [resilience.md](resilience.md). |
| `wait_for_swarm_progress(swarmId, timeoutSeconds=15)` | Blocks server-side until phase change, task-completion count change, or terminal state, then returns a fresh summary (with recommendation). Use in a poll loop instead of repeated `get_swarm_summary` calls. Timeout clamped to 1..60 s. |
| `list_tasks(swarmId, status?, worker?)` | Tasks with optional case-insensitive status and worker filters. |
| `list_agents(swarmId)` | Agents on the swarm team, with roles and task-completion counts. |
| `list_artifacts(swarmId)` | Files under the swarm's work directory. Path-traversal-safe. |
| `read_artifact(swarmId, path)` | Read a single file as text. Truncates at `MaxArtifactBytes`. Rejects `..` and absolute paths via `PathSecurity.TryResolveSafePath`. |
| `get_swarm_templates()` | Templates available on disk (`key`, `name`, `description`). |

### Lifecycle (write)

| Tool | Purpose |
|---|---|
| `create_swarm(goal, templateKey?, context?)` | Create and enqueue a swarm. Optional `context` is a free-form key/value map exposed to custom tools via the run context (see [template-custom-tools.md](template-custom-tools.md)). Returns the new `swarmId`. |
| `cancel_swarm(swarmId)` (`Destructive=true`) | Cancel a running swarm, transitioning it to the Cancelled phase. |

### Recovery (write) — handler-backed

These tools call the same transport-agnostic handler (`ISwarmInterventionHandler`) that the REST `/continue`, `/smart-continue`, `/skip`, and `/mark-as-awaiting-intervention` endpoints use. Every invocation writes a canonical audit transition (`user_continue`, `user_smart_continue`, `user_smart_continue_no_failures`, `user_skip`, `user_mark_for_intervention`) with `actor="mcp"`, consumes retry budget where appropriate, and releases any lock the caller holds. External agents and the admin UI see the same state afterwards. See [resilience.md](resilience.md) for the mental model.

All four return the `RecoveryActionResult` record: `{ ok, statusCode, action, code?, message, currentPhase, recommendation? }`. The `recommendation` field is a fresh snapshot computed *after* the attempt, so a caller whose action was rejected can read the rationale and pick the next action in the same turn without a follow-up `get_swarm_recommendation` call.

| Tool | Purpose |
|---|---|
| `continue_swarm(swarmId)` | Deterministic resume: retries Failed-with-budget tasks and/or picks up viable Pending work. Does NOT invoke the leader. Prefer this over `signal_continue` for any external-agent or operator-driven recovery. |
| `smart_continue_swarm(swarmId)` | Leader-driven recovery: invokes the leader with failure context to produce a reset/add/abandon plan. When there are zero failures but viable open work, short-circuits to Executing with reason `user_smart_continue_no_failures` instead of calling the advisor. |
| `force_synthesis_swarm(swarmId)` (`Destructive=true`) | Abandons remaining open work (maps to the handler's Skip) and jumps into Synthesizing. The synthesis agent produces a report from whatever Completed tasks exist. |
| `mark_swarm_awaiting_intervention(swarmId)` | Flips a `Failed` swarm to `AwaitingIntervention` so the recovery actions become legal. Does NOT resume on its own. |

### Low-level dispatch signals

These two bypass the handler and write no state transitions — they only poke the orchestrator loop. Retained for advanced / diagnostic use. Unless you specifically need the raw dispatch primitive, prefer the audited recovery tools above.

| Tool | Purpose |
|---|---|
| `signal_continue(swarmId)` | Raw dispatch signal — wakes an orchestrator blocked in `EnterSuspendWaitAsync`. No state transition, no retry-budget bookkeeping. |
| `signal_skip(swarmId)` | Raw dispatch signal — tells the orchestrator to stop running rounds. No state transition. |

### Notes on response shape and errors

All tools use MCP structured content (`UseStructuredContent=true`), so responses are typed records rather than stringified JSON. Models that understand structured tool results get the fields directly.

Unrecoverable errors (swarm not found, invalid arguments, filesystem path failures) are raised as `McpException` and surface to the caller as standard MCP tool errors — do not try to parse error text out of the structured payload.

**Recoverable rejections on the recovery tools** — for example Continue rejecting with `no_retry_budget`, Smart Continue folding the advisor's null into `repair_failed`, or a `terminal_state` guard — are returned as `RecoveryActionResult { ok: false, code, message, recommendation }` rather than thrown. This lets the agent read the recommendation on the rejected response and pick a different action without a second round trip.

## Consuming From a Tool-Calling Agent

Any tool-calling model connected to the endpoint through an MCP client can drive swarms. Chat-completion tool calling is **turn-based**: once the agent emits a user-visible message, the turn ends and the MCP connection is disposed. This means the agent cannot perform an open-ended "monitor until done" loop across turns. Use this pattern instead.

### Turn-based polling pattern

1. **Start.** Call `get_swarm_templates` → pick a template → `create_swarm` → `wait_for_swarm_progress(swarmId, 5)` once in the same turn to move past Starting. Respond with `swarm_id=<guid>` on its own line plus the current phase. Tell the user to ask for progress anytime.
2. **Progress checks.** On any follow-up ("progress", "status", "how is it going"), recover the `swarm_id` by scanning prior assistant messages for the literal `swarm_id=<guid>` line, then call `wait_for_swarm_progress(swarmId, 15)` and report the delta.
3. **Recovery.** When a wait returns `phase=AwaitingIntervention` or `NeedsDiagnosis`, the summary's `recommendation` tells you what to do. Call the recommended action (`continue_swarm`, `smart_continue_swarm`, `force_synthesis_swarm`) with the same `swarmId`. The returned `RecoveryActionResult` includes `currentPhase` (typically `Executing` or `Synthesizing` on success) and a fresh `recommendation` — if `ok=false`, read the new recommendation's `recommendedAction` and retry with that. You are not required to follow the recommendation, but it is the server's best opinion.
4. **Completion.** When a wait returns a terminal phase, call `read_artifact(swarmId, "synthesis-report.md")` and surface the report. `get_swarm_summary` exposes this path as `primaryArtifactPath` once the swarm reaches Synthesizing/Complete.

### Keep the swarm_id across turns

Instruct the agent to include `swarm_id=<guid>` on its own line in every response. On the next turn the full assistant message history is replayed to the model, so the agent can recover the id by scanning its prior messages. This replaces per-user state in the host — the id is carried inside the chat transcript itself.

## Verification

Build the solution (warnings are errors — the build must be clean):

```bash
dotnet build Swarmwright.slnx
```

Unit tests for the tool surface live under `tests/Swarmwright.Tests/McpServer/`.

Manual exercise against a running host that has registered the server (`AddSwarmMcpServer` + `MapSwarmMcpServer`, defaults to `SwarmMcp:AuthMode=None`):

1. Run your host. The example host at `tests/Swarmwright.Example.WebHost` listens on `https://localhost:7001`; note it does not register the MCP server out of the box, so add the two calls first if you want to exercise it there.
2. Point an MCP Inspector or any MCP client at your host's configured `EndpointPath` (default `https://<host>/swarm/mcp`) with transport `StreamableHttp`.
3. Run `tools/list` — expect the 18 Swarm tools listed in the catalog (plus any host-registered extras). Call `get_swarm_templates` to verify templates load, then `create_swarm` to drive an actual run.
4. To exercise the recovery surface, create a swarm, force a task failure, then call `get_swarm_recommendation` followed by the recommended action (`continue_swarm` / `smart_continue_swarm` / `force_synthesis_swarm`) — confirm the returned `currentPhase` flipped to `Executing` or `Synthesizing` as expected.

## Known Limitations

- **In-turn streaming is not supported.** The agent cannot stream live swarm progress deltas across chat turns with only MCP. The `wait_for_swarm_progress` tool gives good UX for check-in-style monitoring, but a fire-and-forget stream from the server to a chat surface would require bridging the swarm event bus into a chat streaming pipeline — a separate feature.
- **Loopback HTTPS trust.** When an MCP client and the MCP server run in the same host and the client calls the loopback endpoint (for example `https://localhost:7001/swarm/mcp`), the dev cert must be trusted (`dotnet dev-certs https --trust`).
- **`ModelContextProtocol` pinned at 1.0.0.** `Directory.Packages.props` pins both `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` to `1.0.0`. Floating higher pulls in newer versions of several `Microsoft.Extensions.*` packages; the server stays at 1.0.0 to stay aligned with the MCP client code used elsewhere in the solution (the template tool MCP clients). Revisit when the client side is ready to upgrade together.
