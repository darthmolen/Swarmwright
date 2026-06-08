# Swarmwright — Genericize the CSAT C# Swarm into a Framework-Agnostic Library (revised post-review)

## Context

We built a Python copilot swarm (`~/dev/copilot-sdk-multi-agent-swarm`), then a company-specific C#
swarm from it inside the CSAT server-agent solution. That C# swarm (`CSAT.IT.AI.Agent.Swarm*`) is a
production-grade multi-agent orchestration engine welded to CSAT-internal packages and a CSAT "AI
Agent" specification. We are extracting it into **Swarmwright** — a clean, framework-agnostic,
open-source .NET 10 / C# 14 library — and **going back to native Microsoft Agent Framework (MAF)
agents** instead of the CSAT custom agent layer.

**Source of truth for the port:** `/mnt/c/dev/other/ai-agents/server-agent/src` (Windows
`C:\dev\other\ai-agents\server-agent\src`). The destination repo (`/home/smolen/dev/Swarmwright`) is
scaffolded: `global.json` (net10, MSTest.Sdk), `Directory.Packages.props`, `stylecop.json`, and
`src/` + `tests/` with `Directory.Build.props` enforcing warnings-as-errors, `AnalysisMode=all`, XML
docs in `src`, StyleCop enforced in `src`/relaxed in `tests`.

> **Grounding corpus (populated 2026-06-07):** `research/` now contains the full grounding source:
> - `research/csat-server-agent/` — the CSAT source copied from the `/mnt/c` mirror (all swarm
>   projects + `Directory.Build/Packages.props` + verified seam files: `SwarmAgent.cs`, AzureOpenAI
>   `ServiceCollectionExtensions.cs`, `SwarmRunCompletedNotification(+Consumer)`).
> - `research/agent-framework/` — Microsoft Agent Framework .NET source (`Microsoft.Agents.AI`,
>   `.Abstractions`, `.AzureOpenAI`, Workflows) — ground truth for seams #1/#2 and the adapter.
> - `research/dotnet-extensions/` — `Microsoft.Extensions.AI` + `.Abstractions` — ground truth for
>   the Abstractions dependency and the native `IChatClient` pipeline.
>
> This resolves the original review concern (Critical #1: `research/` was empty and paths pointed at
> nothing). All cited CSAT files are verified present; confirm real MAF/Extensions APIs against these
> clones rather than guessing.

**Outcome:** a buildable Swarmwright solution that runs a swarm end-to-end (core engine + workflows +
MCP server + observability REST/SSE API + React admin SPA + all three templates), with the CSAT
dependency layer replaced by native MAF/`Microsoft.Extensions.AI` and an in-process channel-based
event pipeline.

## Decisions (locked with the user)

1. **Architecture**: thin `Swarmwright.Abstractions` + `Swarmwright.MicrosoftAgentFramework` adapter
   on **native `Microsoft.Agents.AI`** types. CSAT custom agent layer
   (`CSAT.IT.AI.Agent[.AzureOpenAI|.Workflows]`) is replaced.
2. **Scope**: one milestone, full vertical slice — core, workflows, MCP server, observability API,
   React admin SPA, all 3 templates.
3. **Persistence** *(revised — was "as-is")*: **Postgres + SQLite both first-class.** Postgres keeps
   the existing Npgsql/jsonb migrations; SQLite gets its **own provider-specific migrations
   assembly** (no jsonb, relational column types). InMemory remains for unit tests. See Persistence
   section.
4. **Domain assets**: bring all templates over as-is (incl. Azure), keep Entra/MSAL auth in the SPA,
   keep Azure Blob archival. Multi-platform auth refactor deferred.
5. **Quality**: `csharp-quality-developer` for all C# (full StyleCop in `src`, relaxed in `tests`);
   `test-driven-development` for all new/changed logic.
6. **Messaging replacement**: `System.Threading.Channels` + a `BackgroundService` consumer (semantics
   specified in seam #4).
7. **SPA packaging** *(new, was Risk #3)*: **MSBuild-integrated** — the React build runs via an
   MSBuild target in the hosting package and ships as ASP.NET **static web assets**.
8. **Staging granularity** *(new, was Important #1)*: the 14-stage spine is the **roadmap**; each
   stage is decomposed into session-sized tasks via `writing-plans` when its turn comes.

## Package responsibility matrix (locked — resolves Critical #3)

| Package | Responsibility | May depend on | Must NOT depend on |
| --- | --- | --- | --- |
| `Swarmwright.Abstractions` | Interfaces/DTOs only: `ISwarmAgent`, tool contracts, swarm/task models, event contracts. *(reconfirm exact deps during Stage 1 — Minor #1.)* | `Microsoft.Extensions.AI.Abstractions` | ASP.NET, EF Core, MAF |
| `Swarmwright` (core) | Orchestrator, manager, dispatcher, state machine, EF persistence, tools, templates, events, intervention, refinement, telemetry, archival. **No ASP.NET.** | Abstractions, EF Core, `Microsoft.Extensions.*` | ASP.NET Core |
| `Swarmwright.MicrosoftAgentFramework` | MAF adapter: native `IChatClient` factory (Azure OpenAI), `ISwarmAgent` on `Microsoft.Agents.AI`. | Abstractions, `Microsoft.Agents.AI`, `Azure.AI.OpenAI` | ASP.NET Core |
| `Swarmwright.AspNetCore` *(name locked — Minor #2)* | **All** ASP.NET DI extensions + REST/SSE endpoints + archival background-service wiring + SPA static-asset hosting. | core, adapter, ASP.NET Core | — |
| `Swarmwright.Workflows` | `SwarmExecutor<TOutput>` on `Microsoft.Agents.AI.Workflows`. | core, MAF Workflows | — |
| `Swarmwright.McpServer` | `ModelContextProtocol.AspNetCore` + `Microsoft.Identity.Web`. | core, adapter | — |
| `Swarmwright.Templates.*` | DeepResearch (canonical sample), AzureSolutionsAgent, MicrosoftDeepResearch — content as-is. | core | — |

Naming: `CSAT.IT.AI.Agent.Swarm` → `Swarmwright`, sub-namespaces preserved
(`Swarmwright.Orchestration`, `Swarmwright.Hosting`, `Swarmwright.Templates`, …). Drop "CSAT
Solutions, LLC" file headers (dest disables SA1633/IDE0073).

## The four CSAT seams to replace

1. **LLM factory (`AddCsatAzureOpenAI` → native)** — *critical.* Source
   `CSAT.IT.AI.Agent.AzureOpenAI/Extensions/ServiceCollectionExtensions.cs` builds an
   `AzureOpenAIClient`, wraps it in a Polly `ResilientChatClient`, then
   `.AsIChatClient().AsBuilder().UseFunctionInvocation().Build()`. Reimplement in
   `Swarmwright.MicrosoftAgentFramework` using `Azure.AI.OpenAI` + `Microsoft.Extensions.AI`
   directly (`AddSwarmwrightAzureOpenAI`), porting the resilience wrapper as an internal
   `ResilientChatClient`. Keep config section `AzureOpenAI`
   (Endpoint/ApiKey/DeploymentName, `UseBackgroundResponses`, retry options).
2. **Agent layer (`SwarmAgent` → native MAF)** — reimplement the per-agent conversation loop
   (`CSAT.IT.AI.Agent.Swarm/Orchestration/SwarmAgent.cs`) behind `ISwarmAgent` in the adapter using
   native `Microsoft.Agents.AI` (`ChatClientAgent`/`AIAgent` + threads), with
   `FunctionInvokingChatClient` under the hood. Core talks only to `ISwarmAgent`.
3. **MCP (`IMcpClientFactory`)** — optional seam (resolved via `GetService`, null = skip). Define
   `Swarmwright.Abstractions.IMcpClientFactory` + a default impl over `ModelContextProtocol.Client`.
4. **Messaging (`CSAT.IT.Messaging.Mediate`) → Channels + BackgroundService** — *biggest lift.*
   Mediate carries the post-completion archival notification
   (`Events/SwarmRunCompletedNotification.cs` + `…Consumer.cs`). Replace with an internal
   `ISwarmNotificationPublisher` writing to a `Channel<T>`, drained by a
   `SwarmNotificationBackgroundService`. **Operational semantics (locked — was Important #3):**
   - **Bounded** channel (`Channel.CreateBounded`), capacity configurable; full-channel policy
     `Wait` (back-pressure) so completion events are not silently dropped.
   - **Drain on shutdown**: the `BackgroundService` completes the reader and finishes in-flight
     handlers within the host's `StopAsync` token before exit.
   - **Handler failure isolation**: each registered handler invoked under its own try/catch; a
     throwing handler is logged via `LoggerMessage` and does not stop the drain loop or sibling
     handlers.
   - **Best-effort delivery**: no persistent retry/durable queue (matches the original Mediate
     local/background behavior); failures are logged, not retried.
   - **Observability**: per-event enqueue/dequeue/handler-outcome logs via `LoggerMessage`.
   This mirrors the existing `SwarmManager → Channel → Dispatcher` pattern, so it stays consistent
   and dependency-free. Also drop the `FoundryCredentialType` enum in favor of `Azure.Identity`
   credential types directly in `Archival/SwarmArchiverCredentialFactory.cs`.

## Persistence (resolves Critical #2)

The CSAT swarm registers Postgres/SQLite/InMemory in `Extensions/SwarmServiceExtensions.cs`, but
`Database/Migrations/` is a **single Npgsql/jsonb set** SQLite cannot apply. Target = **both
first-class**:

- **Postgres**: keep the existing Npgsql migration set (jsonb, Npgsql annotations), regenerated under
  the new namespace.
- **SQLite**: a **separate provider-specific migrations assembly** generated against a SQLite
  provider — relational column types instead of jsonb (JSON stored as `TEXT`), no Npgsql
  value-generation annotations. Provider selected at runtime by config, each pointing at its own
  `MigrationsAssembly`.
- **InMemory**: unit tests only (no migrations).
- Resolve this **before Stage 4**: define the two migrations assemblies and the provider-switch in
  DI, then port.

## Staging spine (roadmap — keep solution green; each stage decomposed via `writing-plans` when started)

Port in dependency order, bringing each project's tests alongside it (TDD for reworked logic).
**Per-stage acceptance gate (was Important #2):** every stage ends with `dotnet build` warnings-free
**and** that stage's ported/added test set passing before moving on.

0. **Source mapping** — ✅ corpus populated in `research/` (CSAT + agent-framework +
   dotnet-extensions). Remaining: confirm the reuse-vs-CSAT mapping and the package matrix above
   against the clones. *(was Critical #1.)*
1. **Solution + Abstractions** — `.sln`, `Swarmwright.Abstractions`; wire `Directory.Packages.props`
   with net10 versions; **reconfirm Abstractions deps** (Minor #1).
2. **Eventing + MCP shims** — channel notification pipeline (semantics above) + `IMcpClientFactory`.
3. **Models + state machine** — domain models, enums, `StateTransitionService`, `SwarmStateGuards`.
4. **Persistence** — `SwarmDbContext`, repositories; **two migrations assemblies** (Postgres + SQLite
   per Persistence section).
5. **Tools** — default (read/write/web_fetch), coordination (task_update/inbox_send/inbox_receive/
   task_list), leader tools, path security.
6. **Templates** — `TemplateLoader`, `PromptBuilder`, `AgentDefinition`, goal expansion.
7. **Orchestration** — `SwarmOrchestrator`, `SwarmManager`, `SwarmDispatcherService`, intervention,
   refinement (tests via `ScriptedChatClient`).
8. **MAF adapter** — `Swarmwright.MicrosoftAgentFramework`: native `IChatClient` factory + native
   `ISwarmAgent`.
9. **DI + endpoints** — `Swarmwright.AspNetCore`: REST + SSE, archival background-service wiring,
   SPA static-asset hosting target.
10. **Workflows** — `Swarmwright.Workflows` `SwarmExecutor<TOutput>`; drop `ExecutorGuardRailsBase`
    (CSAT) — reimplement as a thin MAF `Executor` wrapper porting only the eval/guardrail bits we
    need.
11. **MCP server** — `Swarmwright.McpServer`.
12. **Template packages** — DeepResearch, AzureSolutionsAgent, MicrosoftDeepResearch (content).
13. **Admin SPA** — port `swarmwright-admin`, keep MSAL; rename API base + branding; wire the
    **MSBuild-integrated** build target + static web assets (Decision #7).
14. **E2E** — port the example-webhost swarm E2E tests against a Swarmwright sample host.

## Quality & testing approach

- Every `.cs` written/edited goes through `csharp-quality-developer` (CRLF, UTF-8 BOM, `this.`
  prefix, no underscore fields, file-scoped namespaces, XML docs on public `src` types,
  LoggerMessage, brace placement). StyleCop fully enforced in `src`, relaxed in `tests`.
- `test-driven-development` for new/changed logic: port/write the failing test first, watch it fail,
  then implement. Reuse `ScriptedChatClient` / `RecordingChatHistoryProvider`.
- `dotnet build` stays warning-free after each staging phase (per-stage gate).

## Remaining risks & open questions

1. **MAF Workflows coupling** — how much of `ExecutorGuardRailsBase` (eval/validation/metrics) to
   port vs. drop to a thin MAF `Executor` (decided per-need at Stage 10).
2. **Central package versions for net10** — verify net10 compatibility of EF Core, Npgsql,
   `Microsoft.Agents.AI`, ModelContextProtocol, Azure.AI.OpenAI (resolved during Stage 1).
3. **Native MAF agent parity** — reimplementing `SwarmAgent` on `Microsoft.Agents.AI` must preserve
   the coordination protocol (single system message, mandatory task_update/inbox calls, no-poll
   stop). Cover with ported behavior tests.
4. **SQLite migration parity** — jsonb→TEXT and Npgsql-specific value generation must map cleanly in
   the SQLite assembly; validate with a SQLite integration test at Stage 4.

## Verification (classified — was Important #5)

**CI-gated (must pass in CI):**
- `dotnet build` warnings-as-errors green for the full solution.
- `dotnet test` green across `Swarmwright.*.Tests` (unit + workflows), including SQLite integration
  tests via a file/in-memory SQLite DB and Postgres integration tests where a test container is
  available.

**Manual / integration-only (not CI-gated; require live Azure):**
- Run a swarm end-to-end via a sample ASP.NET host using the `deep-research` template against a real
  Azure OpenAI deployment; observe lifecycle through REST/SSE and the admin SPA (task board, agent
  roster, events, intervention).
- Resume-after-eviction: start a swarm, evict, resurrect via `/resume`, confirm it skips completed
  phases.

---

## Review disposition (audit note)

Accepted: Critical #3; Important #2/#3/#5; Minor #1/#2. Merged: Critical #1 (source paths corrected
to `/mnt/c` + Stage 0 mapping checkpoint; the "redesign prerequisite" framing rejected as its premise
— ungrounded design — did not hold). User-decided (flagged): persistence = Postgres + SQLite
first-class; SPA = MSBuild-integrated; staging granularity = roadmap, decompose later.

**Pending writes (blocked by plan mode; apply on approval):** (1) `mv` the reviewed file to
`planning/needs-review/completed/`; (2) write this revised plan to the canonical project plan
location.
