# Swarmwright — Genericize the CSAT C# Swarm into a Framework-Agnostic Library

## Context

We built a Python copilot swarm (`~/dev/copilot-sdk-multi-agent-swarm`), then a company-specific C#
swarm from it inside the CSAT server-agent solution. That C# swarm
(`CSAT.IT.AI.Agent.Swarm*` at `/mnt/c/dev/other/ai-agents/server-agent/`) is a production-grade
multi-agent orchestration engine, but it is welded to CSAT-internal packages and a CSAT "AI Agent"
specification. We are extracting it into **Swarmwright** — a clean, framework-agnostic, open-source
.NET 10 / C# 14 library — and **going back to native Microsoft Agent Framework (MAF) agents** instead
of the CSAT custom agent layer.

The destination repo (`/home/smolen/dev/Swarmwright`) is already scaffolded: `global.json`
(net10, MSTest.Sdk 3.8.3), `Directory.Packages.props` (central package management), `stylecop.json`,
and `src/` + `tests/` with `Directory.Build.props` enforcing warnings-as-errors, `AnalysisMode=all`,
XML docs required in `src`, StyleCop enforced in `src` and relaxed in `tests`. The CSAT source is
mirrored into the git-ignored `research/` folder for reference.

**Outcome:** a buildable Swarmwright solution that runs a swarm end-to-end (core engine + workflows +
MCP server + observability REST/SSE API + React admin SPA + all three templates), with the CSAT
dependency layer replaced by native MAF/`Microsoft.Extensions.AI` and an in-process channel-based
event pipeline.

## Decisions (locked with the user)

1. **Architecture**: thin `Swarmwright.Abstractions` (framework-agnostic agent/tool/swarm
   interfaces) + `Swarmwright.MicrosoftAgentFramework` adapter that implements agents on **native
   `Microsoft.Agents.AI`** types. The CSAT custom agent layer (`CSAT.IT.AI.Agent`,
   `CSAT.IT.AI.Agent.AzureOpenAI`, `CSAT.IT.AI.Agent.Workflows`) is replaced.
2. **Scope**: one milestone, full vertical slice — core, workflows, MCP server, observability API,
   React admin SPA, all 3 templates.
3. **Persistence**: port EF Core (Postgres + SQLite + migrations + resume-after-eviction) as-is.
4. **Domain assets**: bring all templates over as-is (incl. Azure ones), keep Entra/MSAL auth in the
   SPA, keep Azure Blob archival. Multi-platform auth refactor deferred.
5. **Quality**: `csharp-quality-developer` skill for all C# (full StyleCop/editorconfig in `src`,
   relaxed in `tests`); `test-driven-development` for all new/changed logic.
6. **Mediate replacement**: `System.Threading.Channels` + a `BackgroundService` consumer (see below).

## Target solution structure & source → destination mapping

| Source project (`src/`) | Destination project | Notes |
| --- | --- | --- |
| — (new) | `Swarmwright.Abstractions` | Thin interfaces/DTOs: `ISwarmAgent`, tool contracts, swarm/task models, event contracts. Depends only on `Microsoft.Extensions.AI.Abstractions`. |
| `CSAT.IT.AI.Agent.Swarm` | `Swarmwright` (core) | Orchestrator, manager, dispatcher, state machine, EF persistence, tools, templates, events, intervention, refinement, telemetry, archival, DI + REST/SSE endpoints. |
| — (split out of core) | `Swarmwright.MicrosoftAgentFramework` | MAF adapter: native `IChatClient` creation (Azure OpenAI), `ISwarmAgent` impl on `Microsoft.Agents.AI`. |
| `CSAT.IT.AI.Agent.Swarm.Workflows` | `Swarmwright.Workflows` | `SwarmExecutor<TOutput>` on `Microsoft.Agents.AI.Workflows`; drop `ExecutorGuardRailsBase` (CSAT) — reimplement as a thin MAF `Executor` wrapper (port only the eval/guardrail bits we actually need). |
| `CSAT.IT.AI.Agent.Swarm.UI` | `Swarmwright.AspNetCore` (or `.Hosting`) | UI/registration DI extensions; replace `AIAgentRegistrationBuilder` hook with a plain Swarmwright DI extension. |
| `CSAT.IT.AI.Agent.Swarm.McpServer` | `Swarmwright.McpServer` | `ModelContextProtocol.AspNetCore` + `Microsoft.Identity.Web`. |
| `.Templates.DeepResearch` | `Swarmwright.Templates.DeepResearch` | Generic, ships as the canonical sample. |
| `.Templates.AzureSolutionsAgent` | `Swarmwright.Templates.AzureSolutionsAgent` | Brought as-is. |
| `.Templates.MicrosoftDeepResearch` | `Swarmwright.Templates.MicrosoftDeepResearch` | Brought as-is. |
| `csat-it-swarm-admin` | `src/swarmwright-admin` | React/TS SPA; keep MSAL/Entra auth. |
| `tests/*Swarm*` | `tests/Swarmwright.*.Tests` | MSTest + FluentAssertions + Moq; shared doubles `ScriptedChatClient`, `RecordingChatHistoryProvider`. |

Naming: `CSAT.IT.AI.Agent.Swarm` → `Swarmwright`, sub-namespaces preserved
(`Swarmwright.Orchestration`, `Swarmwright.Hosting`, `Swarmwright.Templates`, …). Drop the
"CSAT Solutions, LLC" file headers (dest disables SA1633/IDE0073).

## The four CSAT seams to replace

1. **LLM factory (`AddCsatAzureOpenAI` → native)** — *critical*. Source:
   `research/.../CSAT.IT.AI.Agent.AzureOpenAI/Extensions/ServiceCollectionExtensions.cs` builds an
   `AzureOpenAIClient`, wraps in a Polly `ResilientChatClient`, then
   `.AsIChatClient().AsBuilder().UseFunctionInvocation().Build()`. Reimplement in
   `Swarmwright.MicrosoftAgentFramework` using `Azure.AI.OpenAI` + `Microsoft.Extensions.AI`
   directly (`AddSwarmwrightAzureOpenAI`), porting the resilience wrapper as an internal
   `ResilientChatClient`. Config section `AzureOpenAI` (Endpoint/ApiKey/DeploymentName,
   `UseBackgroundResponses`, retry options) kept.
2. **Agent layer (`SwarmAgent` → native MAF)** — reimplement the per-agent conversation loop
   (`research/.../Orchestration/SwarmAgent.cs`) behind `ISwarmAgent` in the adapter using native
   `Microsoft.Agents.AI` (`ChatClientAgent`/`AIAgent` + threads), with `FunctionInvokingChatClient`
   under the hood. Core orchestration talks only to `ISwarmAgent`.
3. **MCP (`IMcpClientFactory`)** — optional seam (resolved via `GetService`, null = skip). Define
   `Swarmwright.Abstractions` `IMcpClientFactory` and a default impl over
   `ModelContextProtocol.Client`.
4. **Messaging (`CSAT.IT.Messaging.Mediate`) → Channels + BackgroundService** — **the biggest
   lift.** Mediate is used for the post-completion archival notification
   (`SwarmRunCompletedNotification` + `…Consumer`, background/local execution). Replace with an
   in-process pipeline: an internal `ISwarmNotificationPublisher` writing to a
   `System.Threading.Channels.Channel<T>`, drained by a `SwarmNotificationBackgroundService`
   (`BackgroundService`) that dispatches to registered handlers (e.g. the archiver) off the
   dispatcher thread, best-effort. This mirrors the existing `SwarmManager → Channel → Dispatcher`
   pattern already in the code, so it is consistent and dependency-free. Also drop the
   `FoundryCredentialType` enum in favor of `Azure.Identity` credential types directly in the
   archival credential factory.

## Staging spine (keep the solution green at every step)

Port in dependency order, bringing each project's tests alongside it (TDD for any reworked logic —
write/port the failing test first, then the code):

1. **Solution + Abstractions** — create `.sln`, `Swarmwright.Abstractions` (interfaces/DTOs), wire
   `Directory.Packages.props` with net10 versions.
2. **Eventing + MCP shims** — channel-based notification pipeline + `IMcpClientFactory` abstraction.
3. **Models + state machine** — domain models, enums, `StateTransitionService`, `SwarmStateGuards`
   (+ their tests).
4. **Persistence** — EF Core `SwarmDbContext`, repositories; **regenerate migrations** (Postgres
   primary; SQLite via a regenerated migration set — see risks).
5. **Tools** — default (read/write/web_fetch), coordination (task_update/inbox_send/inbox_receive/
   task_list), leader tools, path security (+ tests).
6. **Templates** — `TemplateLoader`, `PromptBuilder`, `AgentDefinition`, goal expansion (+ tests).
7. **Orchestration** — `SwarmOrchestrator`, `SwarmManager`, `SwarmDispatcherService`, intervention,
   refinement (+ tests, using `ScriptedChatClient`).
8. **MAF adapter** — `Swarmwright.MicrosoftAgentFramework`: native `IChatClient` factory + native
   `ISwarmAgent`.
9. **DI + endpoints** — `Swarmwright.AspNetCore`: REST + SSE, archival background service wiring.
10. **Workflows** — `Swarmwright.Workflows` `SwarmExecutor<TOutput>` on MAF Workflows.
11. **MCP server** — `Swarmwright.McpServer`.
12. **Template packages** — DeepResearch, AzureSolutionsAgent, MicrosoftDeepResearch (content).
13. **Admin SPA** — port `swarmwright-admin`, keep MSAL; rename API base + branding.
14. **E2E** — port the example-webhost swarm E2E tests against a Swarmwright sample host.

## Quality & testing approach

- Every `.cs` written/edited goes through `csharp-quality-developer` (CRLF, UTF-8 BOM, `this.`
  prefix, no underscore fields, file-scoped namespaces, XML docs on public `src` types, LoggerMessage
  patterns, brace placement). StyleCop fully enforced in `src`, relaxed in `tests`.
- `test-driven-development` for new/changed logic: port or write the failing test first, watch it
  fail, then implement. Reuse `ScriptedChatClient` / `RecordingChatHistoryProvider` for deterministic
  agent tests.
- `dotnet build` must stay warning-free (warnings-as-errors) after each staging phase.

## Risks & open questions

1. **EF migrations are Npgsql-only** (`jsonb`, Npgsql value-generation annotations). SQLite needs a
   freshly regenerated migration set (likely a separate provider-specific migrations assembly).
   *Top risk.* Confirm whether SQLite is a first-class target or test-only.
2. **MAF Workflows coupling** — `ExecutorGuardRailsBase` (CSAT) carries eval/validation/metrics we
   may not need; decide how much to port vs. drop to a thin MAF `Executor`.
3. **SPA ↔ `dotnet build` integration** — does the React build run via MSBuild target/static assets,
   or stay a separate `npm` build? Decide packaging.
4. **Central package versions for net10** — several CSAT packages pinned to older TFMs; verify net10
   compatibility (EF Core, Npgsql, Microsoft.Agents.AI, ModelContextProtocol, Azure.AI.OpenAI).
5. **Native MAF agent parity** — reimplementing `SwarmAgent` on `Microsoft.Agents.AI` must preserve
   the coordination protocol (single system message, mandatory task_update/inbox calls, no-poll
   stop). Cover with ported behavior tests.

## Verification

- `dotnet build` (warnings-as-errors) green for the full solution.
- `dotnet test` green across `Swarmwright.*.Tests` (unit + workflows + integration).
- Run a swarm end-to-end via a sample ASP.NET host using the `deep-research` template against a real
  Azure OpenAI deployment; observe lifecycle through the REST/SSE API and the admin SPA (task board,
  agent roster, events, intervention).
- Integration test resume-after-eviction: start a swarm, evict, resurrect via `/resume`, confirm it
  skips completed phases.

---

## Plan Review

**Reviewed:** 2026-06-07 08:00
**Reviewer:** Claude Code (plan-review-intake)

### Strengths

- **"The four CSAT seams to replace"** is the strongest part: it identifies the real architectural fault lines instead of planning a blind namespace rename.
- **"Staging spine"** is mostly dependency-ordered and tries to keep the solution buildable throughout.
- **"Quality & testing approach"** aligns well with `CLAUDE.md`: central package management, .NET 10/C# 14, MSTest, StyleCop, warnings-as-errors, TDD.
- **"Risks & open questions"** usefully surfaces real risks instead of hiding them.

### Issues

#### Critical (Must Address Before Implementation)

1. **Context / source validation mismatch**
   - **Reference:** Context; "Target solution structure"; "The four CSAT seams to replace"
   - **Problem:** `CLAUDE.md` says the *first real step* is to read the CSAT source and map reusable vs. CSAT-coupled pieces before designing the public surface. But `research/` is empty, and `src/` / `tests/` only contain scaffolding files. The plan still hard-locks package boundaries and references specific source files under `research/...` that are not present.
   - **Why it matters:** The architecture is being frozen without verifiable source input. That risks designing the wrong abstractions and package cuts.
   - **Suggested fix:** Add a prerequisite phase: populate/verify the reference source in `research/`, produce a reusable-vs-CSAT mapping, then finalize package boundaries and public APIs.

2. **Persistence strategy is unresolved but treated as locked**
   - **Reference:** Decisions #3; Staging step 4; Risks #1
   - **Problem:** The plan says to port EF Core persistence "as-is," but also says SQLite likely needs a separate regenerated migration strategy and asks whether SQLite is first-class or test-only.
   - **Why it matters:** This affects project layout, migrations assemblies, test design, and supported scenarios. It blocks implementation rather than just posing a risk.
   - **Suggested fix:** Decide the provider matrix up front (Postgres-only vs Postgres+SQLite), and specify the exact migration strategy before step 4.

3. **Package boundaries are inconsistent**
   - **Reference:** Mapping rows for `Swarmwright`, `Swarmwright.AspNetCore`; Staging steps 7–9
   - **Problem:** The plan says `Swarmwright` core contains DI + REST/SSE endpoints, but later moves DI + endpoints into `Swarmwright.AspNetCore` and even leaves the package name undecided (`.AspNetCore` or `.Hosting`).
   - **Why it matters:** This makes the dependency graph unclear and risks circular references or API churn mid-port.
   - **Suggested fix:** Lock package responsibilities first: core orchestration in `Swarmwright`; ASP.NET-specific DI/endpoints in one hosting package; adapter in `Swarmwright.MicrosoftAgentFramework`.

#### Important (Should Address)

1. **Tasks are too large to be reliably executable in one session**
   - **Reference:** Staging steps 5–13
   - **Problem:** Items like "Tools," "Templates," "Orchestration," "Admin SPA," and "E2E" are multi-day workstreams, not session-sized tasks.
   - **Why it matters:** This makes progress tracking and implementation review hard, and increases rework risk.
   - **Suggested fix:** Split each stage into smaller, file/API-level tasks with explicit outputs.

2. **Verification is only global, not phase-specific**
   - **Reference:** "Staging spine"; "Verification"
   - **Problem:** The plan says to keep the solution green at every step, but only defines end-state verification.
   - **Why it matters:** There is no concrete acceptance gate for each stage.
   - **Suggested fix:** Add per-stage checks (build/test commands, expected passing test sets, smoke checks).

3. **Channel-based notification replacement lacks operational semantics**
   - **Reference:** CSAT seam #4
   - **Problem:** The replacement is described as "best-effort," but the plan does not define bounded vs. unbounded channels, shutdown draining, retry behavior, handler failure isolation, or observability.
   - **Why it matters:** Archival/completion events can be lost or fail silently.
   - **Suggested fix:** Specify queue semantics, error handling, retry/logging policy, and shutdown behavior.

4. **SPA build/package integration remains undecided despite being in milestone scope**
   - **Reference:** Risks #3; Outcome; Staging steps 9, 13
   - **Problem:** The plan includes the React SPA in the milestone but leaves packaging/integration unresolved.
   - **Why it matters:** This impacts CI, `dotnet build`, distribution, and local dev workflow.
   - **Suggested fix:** Choose one model now: separate npm build, or MSBuild-integrated static asset pipeline.

5. **Automated vs manual verification is blurred**
   - **Reference:** Verification bullets
   - **Problem:** `CLAUDE.md` emphasizes `dotnet build` / `dotnet test`, but the plan also requires a real Azure OpenAI end-to-end run.
   - **Why it matters:** Without classification, CI expectations are unclear.
   - **Suggested fix:** Mark which checks are CI-gated vs manual/integration-only.

#### Minor (Consider)

1. **Abstractions dependency may be over-specified too early**
   - **Reference:** Mapping row for `Swarmwright.Abstractions`
   - **Problem:** "Depends only on `Microsoft.Extensions.AI.Abstractions`" may be right, but it is not validated from the current repo state.
   - **Why it matters:** Could leak framework assumptions into the thin abstractions package.
   - **Suggested fix:** Reconfirm after the source-mapping phase.

2. **Naming is still unsettled in a few places**
   - **Reference:** `Swarmwright.AspNetCore (or .Hosting)`
   - **Problem:** Package naming is left open.
   - **Why it matters:** Small, but avoidable churn.
   - **Suggested fix:** Pick one before implementation starts.

### Recommendations

- Insert a **source-mapping prerequisite** before architecture lock-in.
- Resolve **provider support + migrations** before any persistence work.
- Rewrite the staging plan into **smaller executable tasks with per-stage verification**.
- Finalize the **package boundary matrix** before project creation.

### Assessment

**Implementable as written?** With fixes

**Reasoning:** The direction is solid, but the plan freezes architecture before the referenced CSAT source is actually available in this repo, and it leaves blocking decisions unresolved around persistence and package boundaries. Once those are fixed, it becomes implementable.
