# Swarmwright

A **framework-agnostic multi-agent swarm toolkit** for .NET 10 / C#.

Swarmwright is the coordination layer for agent *swarms* — the plumbing that turns a pile of
individual AI agents into a governed system that gets work done. It provides the orchestration
patterns, the durable state, the HTTP/MCP surface, and an admin UI, while staying independent of
any single agent framework: your agents plug in through a thin adapter. Concretely, that adapter is
an `IChatClient` (`Microsoft.Extensions.AI`) — see [docs/abstractions.md](docs/abstractions.md) for
exactly what a model or persistence adapter has to implement.

> **Status: early development.** The core orchestration engine, persistence, HTTP/SSE API, MCP
> server, and admin SPA are in place and covered by tests. Public API surface is still firming up
> and may change between commits.

## What it does

Swarmwright models three swarm shapes on top of a common engine:

- **Supervisor / worker orchestration** — a supervisor decomposes a goal into tasks and dispatches
  them to workers, tracking progress to a terminal state with human intervention points
  (continue / skip / cancel).
- **Fan-out + aggregate** — run many workers in parallel and combine their output: ensembles,
  voting, and cross-verification.
- **Continuous background swarms** — queue-drained workers for long-running reconciliation,
  compaction, and enrichment.

What sets a Swarmwright swarm apart from a fixed workflow or a single planner agent is **how the
plan is formed**. For each goal the leader agent emits an explicit task **DAG** — tasks with declared
`blockedByIndices` dependencies — so independent work runs in parallel and the whole plan is
inspectable *before* execution. Unlike a static workflow graph (wired at author time), the DAG is
generated per prompt; unlike a free-form planner that keeps a prose ledger, it is a structured
artifact constrained by the template's declared roles and tools. You get the adaptivity of an LLM
planner with the legibility and parallelism of a declared graph — and, because the template is
versioned, a bound on what the leader can produce.

Around that engine it ships:

- A **REST + Server-Sent Events API** (`MapSwarmEndpoints`) for driving and observing swarms.
- An **MCP server** (`Swarmwright.McpServer`) that exposes swarm operations as Model Context
  Protocol tools, so other AI agents can create and steer swarms.
- A **React admin SPA** (`swarmwright-admin`) for real-time monitoring and control, served from any
  ASP.NET host with MSAL authentication.
- Pluggable **persistence** (InMemory by default; SQLite and PostgreSQL providers).
- Ready-made **templates** (deep research, Azure solutions agent) as optional packages.

## Solution layout

| Project | Purpose |
| --- | --- |
| `src/Swarmwright.Abstractions` | Tool-authoring contracts (`ICustomToolProvider`, `[SwarmTool]`). Dependency-light, no logic. |
| `src/Swarmwright` | Orchestration engine, swarm primitives, background pipeline, DI. |
| `src/Swarmwright.AspNetCore` | REST + SSE endpoints, `Swarm.Read`/`Swarm.Write` policies, `/api/spa-config`, the `AddSwarmwright` one-call registration. |
| `src/Swarmwright.McpServer` | Swarm operations exposed as MCP tools. |
| `src/Swarmwright.MicrosoftAgentFramework` | Adapter for the Microsoft Agent Framework. |
| `src/Swarmwright.Database.Sqlite` / `.Postgres` | EF Core persistence providers. |
| `src/Swarmwright.Workflows` | Workflow building blocks. |
| `src/Swarmwright.Templates.*` | Optional swarm templates (deep research, Azure solutions). |
| `src/swarmwright-admin` | React + TypeScript admin SPA (Vite). |
| `tests/Swarmwright.Example.WebHost` | Reference ASP.NET host wiring the whole stack together. |
| `tests/*` | MSTest unit + integration tests. |

## Build & test

Requires the **.NET 10 SDK** (pinned in `global.json`).

```bash
dotnet build      # warnings are errors; must be clean
dotnet test       # MSTest
```

The admin SPA:

```bash
cd src/swarmwright-admin
npm install
npm run dev       # http://localhost:5173, proxies /api to https://localhost:7001
npm test          # vitest
```

## Local-dev quickstart

The example host (`tests/Swarmwright.Example.WebHost`) wires the full stack. It reads configuration
from `appsettings.json`, environment variables, and `dotnet user-secrets` — **not** from `.env`.
The `.env` file feeds `docker-compose.yml` and the `scripts/*` helpers.

1. **Optional infrastructure** — Postgres (the app defaults to InMemory) and a local
   OpenAI-compatible model server:

   ```bash
   cp .env.example .env
   ./scripts/start.sh          # postgres only
   ./scripts/start.sh --gpu    # also start a local vLLM model server
   ```

   On Windows without WSL, use `scripts\start.cmd`. See [scripts/README.md](scripts/README.md).

2. **LLM configuration** — copy the settings from `.env` into user-secrets:

   ```powershell
   pwsh ./scripts/set-user-secrets.ps1              # Azure OpenAI / Foundry (uses MAF_AIF_*)
   pwsh ./scripts/set-user-secrets.ps1 -Provider vllm   # local vLLM (uses VLLM_*)
   ```

3. **Run the host:**

   ```bash
   dotnet run --project tests/Swarmwright.Example.WebHost   # https://localhost:7001
   ```

## Authentication (Entra ID)

The REST API validates JWT bearer tokens and the admin SPA signs users in with MSAL. Both are
backed by a single Entra ID (Azure AD) app registration that exposes the `Swarm.Read` and
`Swarm.Write` delegated scopes and a `Swarm.Admin` app role.

To provision it against your tenant (requires the Azure CLI, logged in with rights to register
applications):

```powershell
pwsh ./scripts/provision-app-registration.ps1   # creates the app reg + a 24-month secret, writes AZURE_AD_* to .env
pwsh ./scripts/set-user-secrets.ps1              # pushes AzureAd:* + SpaConfiguration:* into user-secrets
```

The host serves the SPA's MSAL settings anonymously from `GET /api/spa-config`, so the same SPA
build works across environments — only the configuration changes. When no `AzureAd` configuration
is present the host runs unauthenticated (anonymous endpoints, no `/api/spa-config`), which is fine
for a quick local spin-up.

## Documentation

Full documentation lives in [docs/](docs/README.md). Start there for the swarm overview, then dive
into any area:

| Doc | What it covers |
| --- | --- |
| [docs/README.md](docs/README.md) | Swarm overview, when to use a swarm, built-in templates, and the doc index. |
| [docs/swarm.md](docs/swarm.md) | **Start here for integration.** Program.cs wiring, configuration schema, work-directory isolation, auth policies, and the REST/SSE endpoints. |
| [docs/abstractions.md](docs/abstractions.md) | The abstraction layer — the `IChatClient` model seam, persistence and extension points, and how to write a thin adapter. |
| [docs/template.md](docs/template.md) | Authoring swarm templates — directory layout, YAML metadata, leader/worker prompts, task dependencies, placeholder substitution. |
| [docs/template-custom-tools.md](docs/template-custom-tools.md) | Writing and registering custom domain-specific tools for your templates. |
| [docs/mcp-server.md](docs/mcp-server.md) | The Swarm MCP server — 18 swarm operations as MCP tools, auth modes, and turn-based polling. |
| [docs/admin.md](docs/admin.md) | The `swarmwright-admin` SPA — dashboard, Kanban board, report view, and the intervention UI. |
| [docs/archival.md](docs/archival.md) | Promoting completed/failed run directories to Azure Blob storage. Off by default. |
| [docs/azure-ad-setup.md](docs/azure-ad-setup.md) | Entra ID setup — the app registration, host config, verification, and the production split. |
| [docs/resilience.md](docs/resilience.md) | Transient LLM retry (SDK + Polly) and the Continue / Smart Continue / Force Synthesis / Cancel recovery surface. |
| [docs/state-machine.md](docs/state-machine.md) | The swarm + task state machines and the single write surface (`IStateTransitionService`). |
| [docs/state-swarm-instances.md](docs/state-swarm-instances.md) | `SwarmInstanceState` transitions in detail. |
| [docs/state-task.md](docs/state-task.md) | `TaskState` transitions, dependency resolution, and retry semantics. |

## Contributing

C# is held to strict quality gates: `TreatWarningsAsErrors`, `AnalysisMode=all`,
`EnforceCodeStyleInBuild`, generated XML docs, and full StyleCop. See
[CONTRIBUTING.md](CONTRIBUTING.md) and [CLAUDE.md](CLAUDE.md) for the conventions and repository
guide, and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## License

[MIT](LICENSE).
