# Abstractions & Writing an Adapter

Swarmwright is **framework-agnostic** in a specific, concrete sense: the orchestration engine never
talks to an LLM SDK or an agent framework directly. It depends on a small number of interfaces, and
"adapters" are the packages that satisfy them. This doc names those seams so you can see exactly what
an adapter has to implement — and how thin that really is.

## The layer cake

```
Swarmwright.Abstractions        tool-authoring contracts, no logic
        │
Swarmwright                     the engine: orchestration, state machine, persistence contracts,
        │                       templates, archival, the background pipeline, DI
        ├── Swarmwright.MicrosoftAgentFramework   ← model adapter (the reference IChatClient)
        ├── Swarmwright.Database.Sqlite / .Postgres ← persistence adapters
        ├── Swarmwright.AspNetCore                 ← HTTP/SSE surface + DI entry point
        ├── Swarmwright.McpServer                  ← MCP surface
        └── Swarmwright.Templates.*                ← swarm templates (data, not code)
```

The engine consumes interfaces; the packages under it supply implementations. Swap any one without
touching the engine.

## The model seam — `IChatClient`

This is the seam that makes Swarmwright framework-agnostic, and it is the one most people mean by
"adapter". The engine drives every agent — leader and workers — through
[`IChatClient`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) from
`Microsoft.Extensions.AI`, the .NET standard chat abstraction. There is no Swarmwright-specific LLM
interface to implement.

Concretely, [`SwarmOrchestrator`](../src/Swarmwright/Orchestration/SwarmOrchestrator.cs) takes a
leader `IChatClient` and a `Func<string, IChatClient>` worker factory (so different worker roles can
use different models), and [`SwarmAgent`](../src/Swarmwright/Orchestration/SwarmAgent.cs) wraps one
`IChatClient` per agent conversation.

### Writing a model adapter

An adapter is anything that registers an `IChatClient` in DI. Because the built-in registrations use
`TryAddSingleton`, **a consumer-supplied `IChatClient` registered first wins** — so "writing an
adapter" can be as small as:

```csharp
builder.Services.AddSingleton<IChatClient>(sp => myProvider.AsIChatClient());
```

Anything that exposes a `Microsoft.Extensions.AI` chat client plugs in unchanged: the Azure OpenAI
and OpenAI SDKs (`.AsIChatClient()`), Ollama, Anthropic, Amazon Bedrock, or an agent framework that
surfaces one. Add tool-calling / structured-output middleware with the standard
`ChatClientBuilder` pipeline if your provider needs it.

### The reference adapter

[`Swarmwright.MicrosoftAgentFramework`](../src/Swarmwright.MicrosoftAgentFramework/) is the shipped
default. It is small — three files — and worth reading as the template for your own:

- [`ServiceCollectionExtensions`](../src/Swarmwright.MicrosoftAgentFramework/Extensions/ServiceCollectionExtensions.cs)
  exposes `AddSwarmwrightAzureOpenAI(configuration)` (Azure OpenAI) and
  `AddSwarmwrightOpenAI(endpoint, model, apiKey)` (any OpenAI-compatible server — vLLM, Ollama,
  LM Studio, or OpenAI itself). Both `TryAddSingleton` an `IChatClient`.
- [`ResilientChatClient`](../src/Swarmwright.MicrosoftAgentFramework/SelfHealing/ResilientChatClient.cs)
  decorates the inner client with the two-tier rate-limit retry described in
  [resilience.md](resilience.md).

The one-call [`AddSwarmwright(...)`](../src/Swarmwright.AspNetCore/Extensions/IServiceCollectionExtensions.cs)
entry point registers the Azure OpenAI adapter by default; supply your own `IChatClient` (or call
`AddSwarmwrightOpenAI`) before it to override.

## The persistence seam — `ISwarmRepository`

Durable swarm/task state goes through [`ISwarmRepository`](../src/Swarmwright/) over EF Core. The
engine ships three providers, selected by the `Swarm:Database:Provider` config value:

- **InMemory** (default — no external service),
- **SQLite** ([`Swarmwright.Database.Sqlite`](../src/Swarmwright.Database.Sqlite/)),
- **PostgreSQL** ([`Swarmwright.Database.Postgres`](../src/Swarmwright.Database.Postgres/)).

Each provider package carries its own EF Core migrations. To add a store, provide an EF Core
provider (or a bespoke `ISwarmRepository`) the same way. All state writes funnel through
[`IStateTransitionService`](../src/Swarmwright/), the single write surface — see
[state-machine.md](state-machine.md).

## Extension points (no forking required)

These seams let you extend a swarm without touching the engine:

| Seam | What it's for | Start at |
| --- | --- | --- |
| `ICustomToolProvider` + `[SwarmTool]` / `[SwarmToolProvider]` | Domain tools (DB queries, HTTP APIs, business logic) auto-discovered and offered to workers. | [template-custom-tools.md](template-custom-tools.md) |
| Template packages | The team itself — leader/worker prompts, skills, tool wiring — shipped as data. | [template.md](template.md) |
| `ISkillsProvider` | Resolves the skill files (and skill scripts) a role is granted. | [template.md](template.md) |
| `IMcpClientFactory` | Builds MCP clients for the `mcp_endpoints` a worker declares. | [mcp-server.md](mcp-server.md) |
| `ISwarmRunArchiver` | Where completed run directories are promoted (Azure Blob, or the no-op default). | [archival.md](archival.md) |

The tool-authoring contracts (`ICustomToolProvider`, `ISwarmRunContext`, `SwarmToolAttribute`,
`SwarmToolProviderAttribute`) are the only types in
[`Swarmwright.Abstractions`](../src/Swarmwright.Abstractions/) — a dependency-light package a tool
library can reference without pulling in the whole engine.

## Core domain interfaces

For orientation, the main engine interfaces (each has a built-in implementation registered by
`AddSwarmwright`; override any via DI):

| Interface | Responsibility |
| --- | --- |
| `ISwarmManager` | Top-level facade for creating, monitoring, and controlling swarms. |
| `ISwarmService` | Swarm/task reads and writes against the repository. |
| `ISwarmOrchestrator` / `ISwarmOrchestratorFactory` | Runs a single swarm instance; the factory builds a per-run orchestrator. |
| `IStateTransitionService` | The single write surface for all swarm/task state transitions. |
| `ISwarmRepository` | Durable persistence of swarm and task rows (EF Core). |
| `ITemplateLoader` / `ITeamRegistry` | Loads templates and tracks the available teams. |
| `ISwarmRunArchiver` | Promotes finished run directories to durable storage. |
| `IInboxSystem` | The shared inbox / task board workers coordinate through. |
| `ISwarmEventBus` / `ISwarmEmissionBroker` / `ISwarmObservationSink` | The live event/SSE stream to the admin SPA. |
| `ISwarmNotificationPublisher` / `ISwarmNotificationHandler` | The in-process background notification pipeline (e.g. the archival hook). |
| `ISwarmInterventionHandler` | Executes Continue / Smart Continue / Force Synthesis / Cancel. |
| `ILeaderRepairAdvisor` / `IRecommendedSwarmContinueProvider` | The leader's repair-plan advice and the server-computed recovery `recommendation`. |

## Related

- [swarm.md](swarm.md) — host integration and configuration.
- [resilience.md](resilience.md) — the `ResilientChatClient` retry tiers and the recovery surface.
- [state-machine.md](state-machine.md) — `IStateTransitionService` and the write-through flow.
