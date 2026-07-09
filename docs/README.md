# Swarmwright Documentation — Multi-Agent Orchestration

A **swarm** is an agentic team. You give it a goal, it decomposes the goal into tasks, dispatches
specialist agents to work in parallel where possible, and synthesizes their output into a
deliverable — a research brief, a costed solution design, an investigation report, an audit. The
whole run is observable in real time and steerable by a human when something needs intervention.

Swarms are how you get **deep, multi-perspective work** out of an AI system without writing the
orchestration code yourself. Swarmwright supplies the leader-and-workers model, the template system,
the state machine, the recovery surface, and the admin UI. You supply the team composition (which
experts, what skills, what tools) — usually by picking an off-the-shelf template package,
occasionally by authoring your own.

## When a swarm is the right tool

Reach for a swarm when the work meets one or more of these criteria:

1. **Multiple expert perspectives improve the answer.** A single LLM call gives you one viewpoint; a
   swarm gives you parallel specialists (e.g. an Azure architect, a security expert, and a cost
   analyst all reviewing the same goal) with a final synthesis step that weighs their outputs
   against each other.
2. **The output is more than a chat reply.** Swarms produce **artifacts** — Markdown reports,
   Mermaid diagrams, IaC templates, comparison tables — that a stakeholder can take, read, and act
   on. If you only need a streamed text response, a single agent call is the right surface.
3. **Many heterogeneous inputs need to fuse into a single informed decision.** When the requirements
   are complex and span many data sources — both data and rules — swarms bring those sources
   together through DAG-style task dependencies, many agents, and tools so an informed decision can
   be presented. A swarm would excel at, say, planning a warehouse reorganization where floor maps,
   SKU locations, pick-path movements, and in-flight orders all have to be weighed against each
   other.

For everything else — chatbots, single-shot LLM calls inside a back-end service,
deterministic-pipeline data work — use a single agent.

## How the system works (the short version)

- **A leader agent** interviews the user (where appropriate), refines the goal, and produces a
  phased task plan. Tasks declare their `blockedByIndices` so the framework knows what can run in
  parallel.
- **Specialist worker agents** each pick up tasks they're qualified for. They share an inbox/task
  board, can call MCP tools, and write their work into an isolated work directory. Workers can be
  reused across templates — what changes per template is the **skills** and **tool endpoints**
  assigned to each role.
- **A synthesis pass** combines the worker outputs into a final deliverable. The leader can request
  a second round if the synthesis flags gaps.
- **The whole run is a swarm instance** — durable, resumable, and observable via REST + SSE.
  Operators see the Kanban board, intervene on stuck tasks, and trigger recovery actions from the
  admin SPA or the Swarm MCP server.

For the system architecture and configuration, start at [swarm.md](swarm.md).

## Built-in templates (shipped as NuGet)

Templates are the unit of distribution. Each one bundles the leader prompt, worker prompts, skill
files, and any MCP endpoint config the team needs. Three ship out of the box; consumers install
whichever fits the work and add one line to host setup:

```csharp
builder.Configuration.AddSwarmTemplatePackages();
```

| Template package | Team | What it produces |
| --- | --- | --- |
| **`Swarmwright.Templates.AzureSolutionsAgent`** | Azure Solutions Architect Lead → six specialists (Architect, AI/ML Engineer, Cost Expert, IaC Architect, IaC Developer, Security Expert) drawing on Azure skills (architecture, networking, AKS, ML, security, Entra, cost optimization, etc.). Backed by the `learn-microsoft` MCP endpoint. | Production-ready Azure architecture designs that follow the Well-Architected Framework: service selection with trade-off rationale, Mermaid topology diagrams, networking + private-endpoint design, cost review with approval gate, deployable Bicep / Terraform IaC, security review. The leader runs a short interview before swarming so the team right-sizes the solution instead of over-engineering. |
| **`Swarmwright.Templates.MicrosoftDeepResearch`** | Microsoft-ecosystem research team — documentation researcher, licensing analyst, Azure integration specialist, skeptic. Backed by the `learn-microsoft` MCP endpoint. | Synthesized research briefs on Microsoft technologies, products, and licensing positions, with the skeptic role specifically tasked with surfacing counterarguments and edge cases the primary researcher missed. |
| **`Swarmwright.Templates.DeepResearch`** | General-purpose multi-angle research team — primary researcher, skeptic, data analyst. Self-contained (no required MCP endpoints). | Synthesized research briefs on any topic, produced from independent angles to stress-test the conclusion. Use when the subject isn't Microsoft-specific. |

Authoring your own template (your domain, your experts, your tools) is the design point of the
framework — see [template.md](template.md) for the directory layout and YAML metadata,
and [template-custom-tools.md](template-custom-tools.md) for adding domain-specific tools
(database queries, HTTP APIs, business logic).

## How swarms get driven

- **Programmatically** — POST to the swarm endpoints from any back-end service.
- **By a human** — the `swarmwright-admin` SPA gives operators a real-time dashboard, Kanban task
  board, intervention UI, and report viewer.
- **By external agents** — the Swarm MCP server exposes the swarm operations as MCP tools over HTTP,
  so a separate agent (or any MCP client) can create, monitor, and recover swarms on a user's
  behalf.

## Contents

| Doc | Purpose |
| --- | --- |
| [swarm.md](swarm.md) | **Start here.** Program.cs integration, configuration schema (OpenAI/Azure OpenAI, DB, concurrency), work-directory isolation, auth policies, HTTP endpoints for create/monitor/control. |
| [template.md](template.md) | Authoring swarm templates — directory layout, YAML metadata, leader/worker prompts, tool resolution, task dependencies (`blockedByIndices`), and placeholder substitution. |
| [template-custom-tools.md](template-custom-tools.md) | Writing custom domain-specific tools (database queries, HTTP APIs, business logic) and registering them with the framework for use in your own templates. |
| [mcp-server.md](mcp-server.md) | The Swarm MCP server — swarm operations exposed as MCP tools over HTTP, including the recovery surface (`continue_swarm`, `smart_continue_swarm`, `force_synthesis_swarm`, `mark_swarm_awaiting_intervention`, `get_swarm_recommendation`). Auth modes (None / ApiKey / AzureAD) and turn-based polling patterns for external agents. |
| [admin.md](admin.md) | The admin SPA — real-time dashboard, Kanban task board, inbox/tool-call feed, report view, and the intervention interface that surfaces the server's recommended recovery action. |
| [archival.md](archival.md) | Promoting completed (and failed) run work directories to durable Azure Blob storage — `Swarm:Archival` config, the dev/prod credential model, the no-op fail-safe when unconfigured, the blob layout + `manifest.json`, and security requirements. Off by default. |
| [azure-ad-setup.md](azure-ad-setup.md) | Entra ID setup: the single combined app registration the provisioning script creates (scopes, app role, SPA redirects, secret), host configuration, and verification with sample tokens — plus the production multi-registration split. |
| [resilience.md](resilience.md) | Two layers of resilience: (1) transient — two-tier LLM rate-limit retry (SDK retry policy + Polly), and (2) recovery — the Continue / Smart Continue / Force Synthesis / Cancel actions and the server-computed `recommendation` surface both UI and MCP consumers read. |
| [state-machine.md](state-machine.md) | The swarm + task state machines, the single write surface (`IStateTransitionService`), and the end-to-end sequence for how dependency resolution writes through from memory to DB. |
| [state-swarm-instances.md](state-swarm-instances.md) | `SwarmInstanceState` transitions — per-phase diagram, reason strings, and which component writes each transition. |
| [state-task.md](state-task.md) | `TaskState` transitions — per-round lifecycle, dep-resolution flow, and retry semantics (user Continue vs leader Smart Continue vs abandoned-dep strip). |
