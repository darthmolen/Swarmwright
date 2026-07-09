# Swarm Template Authoring Guide

How to create swarm templates: directory layout, YAML metadata, prompts, tools, MCP integration, and task dependencies.

For application setup, configuration, and deployment, see [swarm.md](swarm.md).

For working examples, see the templates shipped as NuGet packages:

- [`src/Swarmwright.Templates.DeepResearch/templates/deep-research/`](../src/Swarmwright.Templates.DeepResearch/templates/deep-research/) — fan-out pattern (3 parallel workers: primary researcher, skeptic, data analyst), shipped as a NuGet package.
- [`src/Swarmwright.Templates.MicrosoftDeepResearch/templates/microsoft-deep-research/`](../src/Swarmwright.Templates.MicrosoftDeepResearch/templates/microsoft-deep-research/) — Microsoft-ecosystem fan-out (4 parallel workers) backed by the `learn-microsoft` MCP endpoint, shipped as a NuGet package.
- [`src/Swarmwright.Templates.AzureSolutionsAgent/templates/azure-solutions-agent/`](../src/Swarmwright.Templates.AzureSolutionsAgent/templates/azure-solutions-agent/) — multi-phase DAG (design → cost gate → IaC planning → parallel IaC implementation) that combines dependency topology, skills, MCP endpoints, and workdir-based handoff. Shipped as a NuGet package.

To ship a template as a reusable NuGet package, see [Shipping a template as a NuGet package](#7-shipping-a-template-as-a-nuget-package) below.

---

## 1. Template Lifecycle

Every swarm runs four phases:

1. **Plan** — Leader receives `leader.md` + the expanded `goal_template`, calls `create_plan` with a task array.
2. **Spawn** — Orchestrator creates one agent per unique `workerName`, resolves tools.
3. **Execute** — Rounds dispatch runnable tasks to workers concurrently.
4. **Synthesize** — Leader receives `synthesis.md` plus the completed task results, calls `submit_report`.

(An optional QA interview phase, gated per template, can run before planning; it is not required for template authoring and is omitted here.)

---

## 2. Directory Structure

```
templates/
  system-prompt.md                   # shared preamble (optional, prepended to all workers)
  my-template/
    _template.yaml                   # metadata + goal_template
    leader.md                        # leader system prompt (frontmatter + body)
    synthesis.md                     # synthesis prompt (frontmatter optional; only the body is used)
    worker-researcher.md             # worker definitions (frontmatter + body)
    worker-writer.md
```

- The directory name must match the `key` in `_template.yaml` — it is how `TemplateLoader` discovers and loads the template. Keys must match `^[A-Za-z0-9_-]+$` (no path separators or traversal sequences).
- Worker files must follow the `worker-<name>.md` naming pattern; `TemplateLoader` globs `worker-*.md`.
- The `name` field in a worker's frontmatter is what the leader targets with `workerName` in the plan.

---

## 3. File Reference

### `_template.yaml`

The recognized top-level fields are `key`, `name`, `description`, `goal_template`, `allow_default_tools`, and `allow_skill_scripts`. Any other keys are ignored by the loader.

```yaml
key: my-template
name: My Template
description: Short description for the UI
allow_default_tools: true        # default: true — gives workers read/write/web_fetch
allow_skill_scripts: false       # default: false — see Skills (section 10)
goal_template: |
  Build a team to: {user_input}

  Create a researcher and a writer.
  The writer is blocked by the researcher.
```

The `goal_template` is the user message sent to the leader. Use it to steer the leader toward your desired task structure and dependency topology. It supports the `{user_input}` and `{goal}` placeholders (both expand to the user's raw goal text, via `GoalTemplateExpander`).

### `leader.md`

```yaml
---
name: leader
displayName: Team Leader
description: Plans tasks and synthesizes results
---
```

Only the **body** of `leader.md` is used (as the leader's system prompt during planning). The frontmatter is parsed but discarded, so it is purely cosmetic — describe available workers, their roles, and the desired task-decomposition strategy in the body.

### `worker-*.md`

```yaml
---
name: researcher                 # snake_case identifier the leader targets via workerName
displayName: Research Specialist # note: camelCase key
description: Conducts primary source research
tools:                           # explicit whitelist that FILTERS the resolved set (omit = no filter)
  - task_update
  - inbox_send
  - inbox_receive
  - task_list
  - read
  - write
allow_default_tools: true        # omit = inherit from the template-level setting
infer: true                      # default: true — enable LLM inference for this worker
skills:                          # optional, see section 10
  - my-skill
mcp_endpoints:                   # optional, see section 4 (MCP Server Integration)
  - learn-microsoft
custom_tools:                    # optional, see template-custom-tools.md
  - query_database
---
```

The body is the worker's domain prompt. Use `{display_name}` and `{role}` placeholders — they expand to the worker's `displayName` and `description` respectively.

Recognized worker frontmatter fields: `name`, `displayName` (camelCase), `description`, `tools`, `infer`, `skills`, `allow_default_tools`, `mcp_endpoints`, `custom_tools`. Other keys are ignored.

### `synthesis.md`

Frontmatter is optional and ignored; only the body is used as the synthesis system prompt. The framework does **not** substitute placeholders into the synthesis body — instead it appends the completed task results as a separate user message (see [Prompt Assembly](#6-prompt-assembly)). The body must instruct the LLM to call `submit_report` with the final output.

### `system-prompt.md`

Placed at the templates root (not inside a template subdirectory). Shared across all templates. Defines the coordination protocol (task_update workflow, inbox rules). `TemplateLoader` loads it from `{templatesDirectory}/system-prompt.md`, one level above each template dir, and prepends it to every worker prompt.

---

## 4. Tools

Workers receive tools from up to four sources, resolved at spawn time by `SwarmToolFactory.CreateWorkerTools` (or `CreateWorkerToolsAsync` when MCP endpoints are involved).

### Coordination Tools (always available)

| Tool | Purpose |
|------|---------|
| `task_update` | Report task status. Terminal values are `Completed` or `Failed` (the tool also forgives `in_progress`/`InProgress` variants, but only `Completed`/`Failed` are valid final states). |
| `inbox_send` | Send a message to another agent by name (use `to="leader"` to reach the team leader). |
| `inbox_receive` | Pull and remove all messages from the agent's inbox (call once, do not poll). |
| `task_list` | View tasks on the board (your own by default, or `owner='all'`). |

### Default Tools (opt-in)

Available when `allow_default_tools` resolves to `true`. File tools are scoped to the per-swarm work directory:

| Tool | Purpose | Limits |
|------|---------|--------|
| `read` | Read a file from the work directory | 50 KiB max (truncates beyond that) |
| `write` | Write a file to the work directory | 1 MiB max content |
| `web_fetch` | Fetch a public HTTP/HTTPS URL, returns stripped text | 30s timeout, 50 KiB, blocks loopback and private IPs |

> There is no directory-listing tool in the default set — only `read`, `write`, and `web_fetch`. Downstream workers must be told the **exact filenames** to `read` (see [Inter-agent handoff](#5a-inter-agent-handoff-workdir-files-vs-task_updateresult)).

### Tool Resolution

```
effectiveAllowDefaults = worker.allow_default_tools ?? template.allow_default_tools ?? true

tools = coordination
if effectiveAllowDefaults: tools += defaults           # read, write, web_fetch
tools += mcp tools for each worker.mcp_endpoints        # if any (async path)

if worker.tools is set (non-empty):
    tools = tools where name is in worker.tools         # whitelist FILTERS the set
```

The whitelist is a **filter**, not an additive grant: it can only keep tools that were already added. Whitelisting `write` while `allow_default_tools` resolves to `false` yields nothing for `write`, because `write` was never added.

| Template `allow_default_tools` | Worker `allow_default_tools` | Worker `tools` | Effective Tools |
|---|---|---|---|
| `true` | (omitted) | (omitted) | 7 (coordination + defaults) |
| `true` | `false` | (omitted) | 4 (coordination only) |
| `false` | (omitted) | (omitted) | 4 (coordination only) |
| `false` | `true` | (omitted) | 7 (coordination + defaults) |
| `true` | any | `[task_update, write]` | 2 (filtered from the resolved set) |

### Adding Custom Tools

Custom domain-specific tools (database queries, HTTP APIs, business logic) don't require forking the framework. Subclass `CustomToolProvider`, decorate methods with `[SwarmTool("name", "description")]`, and workers opt in via `custom_tools: [...]` in frontmatter. `AddAISwarm` auto-discovers `ICustomToolProvider` implementations and registers each one using the lifetime declared by its `[SwarmToolProvider(ServiceLifetime.X)]` attribute, so there's no separate DI registration step (manual registrations always win).

The `[SwarmTool]` and `[SwarmToolProvider]` attributes and `ICustomToolProvider` live in `Swarmwright.Abstractions.Tools`; the `CustomToolProvider` base class lives in `Swarmwright.Tools`.

See [template-custom-tools.md](template-custom-tools.md) for the full walkthrough covering provider definition, DI lifetimes, dependency-injection patterns, testing, and troubleshooting.

### MCP Server Integration

There are two distinct MCP concerns; do not conflate them.

**1. The Swarmwright MCP server (observing a swarm from the outside).** The `Swarmwright.McpServer` package hosts an MCP tool server that exposes swarm state and control operations to external MCP clients (e.g. Claude Code, Copilot) — tools such as `get_active_swarms`, `get_swarm_status`, `list_tasks`, `list_agents`, `get_swarm_summary`, `create_swarm`, and recovery operations. These are for driving/observing swarms from another system; workers *inside* a swarm do not use them. See [mcp-server.md](mcp-server.md) for the full tool catalog and hosting details.

**2. Giving a worker access to an external MCP server (built into the template system).** Declare the endpoint names a worker should load tools from via `mcp_endpoints` in its frontmatter:

```yaml
---
name: microsoft_researcher
displayName: Microsoft Researcher
description: Authoritative Microsoft product/feature researcher using Microsoft Learn
mcp_endpoints:
  - learn-microsoft
---
```

At spawn time, `CreateWorkerToolsAsync` loads each declared endpoint's tools and adds them to the worker's tool list. The endpoint itself is configured under an `MCPClients` section in configuration (typically a sidecar `appsettings.swarm-<key>.json` — see [Shipping a template](#7-shipping-a-template-as-a-nuget-package)). The `microsoft-deep-research` and `azure-solutions-agent` templates are the reference examples; both declare `mcp_endpoints: [learn-microsoft]` and ship the endpoint config in their sidecar.

---

## 5. Task Dependencies (Blocking)

Dependencies are set **by the leader at runtime** via `blockedByIndices` in the plan. You cannot hardcode them in templates — you steer the leader through `goal_template` and `leader.md`.

### How It Works

1. **Leader creates plan** with `blockedByIndices` on each task (0-based indices into the task array).
2. **Orchestrator maps indices to task IDs** and populates `SwarmTask.BlockedBy` (duplicates are deduplicated at this seam).
3. **`AddTaskAsync`** sets initial status: `Blocked` (if `BlockedBy` is non-empty) or `Pending` (if empty), and persists the blocked-by list.
4. **Each round** only `Pending` tasks are dispatched to workers.
5. **On task transition to a terminal state (`Completed` or `Failed`)**, `StateTransitionService.PromoteDependentsAsync` strips the task's id from every dependent's blocked-by list.
6. **When `BlockedBy` becomes empty**, the dependent transitions from `Blocked` to `Pending` and becomes runnable in the next round.

```
Task 0: BlockedBy=[]     -> Pending  -> Round 1 (runs immediately)
Task 1: BlockedBy=[]     -> Pending  -> Round 1 (runs in parallel with Task 0)
Task 2: BlockedBy=[0,1]  -> Blocked  -> Round 2 (after both 0 and 1 complete)
Task 3: BlockedBy=[2]    -> Blocked  -> Round 3 (after 2 completes)
```

### Dependency Patterns

Use these patterns in `goal_template` and `leader.md` to steer the leader:

**Fan-out (all parallel):**
```yaml
goal_template: |
  Create three independent analysts. All tasks run in parallel.
  No task depends on any other — all have blockedByIndices: [].
```

**Linear pipeline (A -> B -> C):**
```yaml
goal_template: |
  Create: researcher (index 0), writer (index 1), editor (index 2).
  - researcher: blockedByIndices: []
  - writer: blockedByIndices: [0]
  - editor: blockedByIndices: [1]
```

**Fan-in (many -> one):**
```yaml
goal_template: |
  Create 3 analysts (indices 0,1,2) and a synthesizer (index 3).
  Analysts run in parallel with blockedByIndices: [].
  Synthesizer blocked by all three: blockedByIndices: [0, 1, 2].
```

**Diamond (fan-out + fan-in):**
```yaml
goal_template: |
  Create: apex (0), left (1), right (2), merge (3).
  - apex: blockedByIndices: []
  - left: blockedByIndices: [0]
  - right: blockedByIndices: [0]
  - merge: blockedByIndices: [1, 2]
```

**Hourglass (parallel -> gate -> parallel):**
```yaml
goal_template: |
  Create: alpha (0), bravo (1), gate (2), delta (3), echo (4).
  - alpha, bravo: blockedByIndices: []
  - gate: blockedByIndices: [0, 1]
  - delta, echo: blockedByIndices: [2]
```

The `azure-solutions-agent` template is a real, multi-phase example of this: design tasks run in parallel, a cost-gate task is blocked by all designs, an IaC-architect task is blocked by the cost gate, and multiple IaC-developer tasks are blocked by the IaC architect.

### Tips for Reliable Dependencies

- **Be explicit about indices** in `goal_template` — tell the leader exactly which indices to use for `blockedByIndices`.
- **Repeat dependency instructions in `leader.md`** — the leader sees both during planning.
- **Name tasks descriptively** — helps the leader understand which task should block which.
- **Forward references are silently dropped** — `blockedByIndices: [3]` on task 2 is ignored because task 3 doesn't exist yet when task 2 is created.

---

## 5a. Inter-agent handoff: workdir files vs `task_update.result`

Workers have two channels for passing output to downstream workers:

1. **`task_update.result`** — a self-contained status + findings string delivered to the task board. The synthesis phase reads the `result` of every completed task to build the final report. Fine for summaries, conclusions, approval decisions — anything that fits in a single tool-call argument.
2. **Workdir files** — large artifacts (design documents, IaC plans, generated code, data extracts) written via the `write` default tool. Other workers read them via `read`. Required when the artifact is too big to cram into a tool-call argument or needs a persistent form.

Both channels are legitimate. The `deep-research` templates use only `task_update.result`; the `azure-solutions-agent` template uses workdir files because design docs routinely exceed a few KB.

### Required convention when using workdir files

Every *consuming* agent's prompt MUST include an **Inputs** section that:

1. **Names the expected filenames verbatim.** The LLM can't guess `architecture-design.md` from "read the architect's design." This matters even more here because the default tool set has no directory-listing tool — the consumer only has `read`, so it must be handed exact names.
2. **Tells the LLM to `read` each named file.** The `read` tool is available when the template sets `allow_default_tools: true`. Without this pointer the LLM may skip discovery entirely and claim nothing is there.
3. **Specifies failure behavior on missing required inputs** — fail loudly with `task_update(status="Failed", result="Missing upstream design: <filename>")`, don't hallucinate values. The dependency graph should prevent missing inputs in practice; fail visibly when it doesn't.

Upstream *producer* prompts should reciprocally name the exact output filename in their **Deliverables** section (e.g. "Write your analysis to the work directory as `architecture-design.md`"). Consumer prompts then reference those filenames.

### Why this matters

The canonical failure mode when the convention is skipped: a consumer worker runs, the LLM honestly tries to find inputs it wasn't told about, can't, and returns a plausible-sounding message like "I could not find any design or configuration input files in the work directory to review." The swarm suspends, a retry budget is consumed, and the only fix is rewording the prompt. The tools always worked — the LLMs just weren't told which files to read.

### See the `azure-solutions-agent` template for the reference pattern

Its downstream workers (`cost-expert`, `ai-ml`, `iac-architect`, `iac-developer`) each have an **Inputs** section listing the exact filenames tuned to their role. Copy that pattern for any new template that uses workdir-based handoff.

---

## 6. Prompt Assembly

Worker system prompts are composed in layers by `PromptBuilder.ForWorker`:

```
Layer 1: system-prompt.md body (coordination protocol)
Layer 2: Work directory directive (if a work directory exists)
Layer 3: worker-*.md body (with {display_name} and {role} expanded)
Layer 4: skills description fragment (if the worker declares skills)
Layer 5: framework task_update + leader-inbox mandates
```

Layers are joined with `\n\n`. Empty layers are skipped.

The **synthesis** prompt is assembled differently: the `synthesis.md` body is used verbatim as the system message, and the framework appends a separate user message of the form `Task results:\n\n<Subject/Result of each Completed task>`. Placeholders in `synthesis.md` are not expanded by the framework.

### Replacement Variables

| Variable | Available In | Description |
|----------|-------------|-------------|
| `{user_input}` / `{goal}` | `_template.yaml` `goal_template` | User's raw goal text (both tokens expand to the same value) |
| `{display_name}` | `worker-*.md` body | Worker's `displayName` |
| `{role}` | `worker-*.md` body | Worker's `description` |

> The `synthesis.md` body receives no placeholder expansion. Completed task results are supplied automatically as a separate user message, so write the synthesis prompt to expect the results to follow, rather than relying on a `{task_results}` token.

---

## 7. Shipping a template as a NuGet package

Templates can be packaged and distributed as standalone NuGet packages so consumers pick the ones they want without copying files. `Swarmwright.Templates.DeepResearch`, `Swarmwright.Templates.MicrosoftDeepResearch`, and `Swarmwright.Templates.AzureSolutionsAgent` are the reference examples.

### Project layout

```
src/Swarmwright.Templates.<YourTemplate>/
├── Swarmwright.Templates.<YourTemplate>.csproj
├── templates/
│   └── <template-key>/
│       ├── _template.yaml
│       ├── leader.md
│       ├── synthesis.md
│       ├── worker-*.md
│       └── skills/                 # optional, see section 10
└── appsettings.swarm-<template-key>.json
```

No C# code — pure asset delivery.

### csproj pattern

This mirrors the shipped template packages: `templates/**` and the sidecar are packed as `contentFiles` and copied to the consumer's output.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <Description>Swarmwright template package: "&lt;template-key&gt;" — &lt;short description&gt;.</Description>
    <PackageTags>swarmwright;swarm;template;ai-agent</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="templates\**\*"
             Pack="true"
             PackagePath="contentFiles\any\any\templates"
             BuildAction="None"
             PackageCopyToOutput="true"
             CopyToOutputDirectory="PreserveNewest">
      <TargetPath>templates\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
    </Content>
    <Content Include="appsettings.swarm-*.json"
             Pack="true"
             PackagePath="contentFiles\any\any"
             BuildAction="None"
             PackageCopyToOutput="true"
             CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

### Sidecar `appsettings.swarm-<template-key>.json`

Put any configuration the template requires (but the consumer doesn't already have) in the sidecar — most commonly, `MCPClients` entries for endpoints the template's workers declare in their `mcp_endpoints` frontmatter. Leave it as `{}` if the template is self-contained (the `deep-research` sidecar is empty for exactly this reason).

Example (from `Swarmwright.Templates.MicrosoftDeepResearch`):

```json
{
  "MCPClients": {
    "learn-microsoft": {
      "name": "Microsoft Learn",
      "baseUrl": "https://learn.microsoft.com/api/mcp",
      "basePath": "",
      "authType": "None",
      "timeoutSeconds": 30
    }
  }
}
```

### Consumer wiring

Consumers install the package and add one line during host setup, on the configuration builder, before registering the swarm (`AddAISwarm`):

```csharp
builder.Configuration.AddSwarmTemplatePackages();
// ...
builder.Services.AddAISwarm(builder.Configuration, builder.Environment);
```

`AddSwarmTemplatePackages()` is defined in [`Swarmwright.Extensions.ConfigurationBuilderExtensions`](../src/Swarmwright/Extensions/ConfigurationBuilderExtensions.cs). It globs the content root (default `AppContext.BaseDirectory`) for `appsettings.swarm-*.json` sidecars (plus environment-specific `appsettings.swarm-<key>.{Environment}.json` variants) and layers each into the configuration pipeline. The template files themselves are copied into `{bin}/templates/<template-key>/` automatically by the NuGet content-file machinery, where the existing `TemplateLoader` picks them up.

### Shared `system-prompt.md`

The cross-template shared preamble (loaded by `TemplateLoader` from `{templatesDirectory}/system-prompt.md`, one level above each template dir) ships with the core `Swarmwright` package. You do not need to ship one in your template package; if you want to override the default, drop a `templates/system-prompt.md` in your consumer host and it will take precedence at runtime.

### Versioning

Template packages should version alongside the core `Swarmwright` package to keep their contract with `TemplateLoader` aligned. Bump a template package independently only when the template content changes without any framework-side dependency changes.

---

## 8. Mixing local + packaged templates

`TemplateLoader` scans a single directory — `AppContext.BaseDirectory/templates/` by default — and discovers **every** subfolder that contains a `_template.yaml`. NuGet-shipped templates and in-repo local templates coexist in that one folder, so a host can use both at the same time.

**The critical bit:** if your host ships its own templates (e.g. under `host-root/Templates/`), those files do **not** land in the bin output automatically. You must add an MSBuild item to your host csproj — even if you aren't using any NuGet template packages. Without it, your local templates are source-only and `TemplateLoader` never sees them.

### Host csproj — copy local templates to bin

```xml
<ItemGroup>
  <Content Include="Templates\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <TargetPath>templates\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
  </Content>
</ItemGroup>
```

The `<TargetPath>` normalizes to lowercase `templates/` in the output. Without it, Windows (case-insensitive) works fine but Linux sees `Templates/` and `templates/` as separate folders and the loader misses your local templates. Always include it.

### Resulting bin layout

After `dotnet build`, your host's `bin/Debug/net10.0/templates/` looks like a flat merge of every source:

```
bin/Debug/net10.0/templates/
├── system-prompt.md                  (from Swarmwright — core)
├── deep-research/                    (from Swarmwright.Templates.DeepResearch — NuGet)
├── microsoft-deep-research/          (from Swarmwright.Templates.MicrosoftDeepResearch — NuGet)
├── azure-solutions-agent/            (from Swarmwright.Templates.AzureSolutionsAgent — NuGet)
└── my-template/                      (from host-root/Templates/my-template — local)
```

### Collision rules

- **Unique template keys are mandatory.** If your local `Templates/my-template/_template.yaml` has `key: deep-research`, both a NuGet-shipped `deep-research/` and your local copy land in the same `bin/templates/deep-research/` folder. MSBuild's file-copy order between sources is undefined for this case — the result is inconsistent and effectively broken.
- **Your own `system-prompt.md`** — if you drop `Templates/system-prompt.md` in your host, it overwrites the preamble shipped by the core package. Intentional override is fine; accidental collision is the common footgun. Rename or skip the file unless you mean to replace the default.

### Configuration for a local template

If your local template needs configuration the host doesn't already have (e.g. an MCP endpoint in `MCPClients` that its workers reference), you have two options:

1. **Put it in your host's main `appsettings.json`** — simplest. The template is in-repo, so there's no separation-of-concerns argument for a sidecar.
2. **Drop an `appsettings.swarm-my-template.json` next to `appsettings.json`** — matches the NuGet convention and lets `AddSwarmTemplatePackages()` pick it up automatically. You'll need to copy it to the output:

   ```xml
   <ItemGroup>
     <Content Include="appsettings.swarm-*.json">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </Content>
   </ItemGroup>
   ```

   Use this when you plan to extract the template into its own NuGet package later — the sidecar travels with it unchanged.

### Overriding the templates directory

If you want templates in a non-default location (e.g. an external mount or a shared volume), set `Swarm:TemplatesDirectory` in config:

```json
{
  "Swarm": {
    "TemplatesDirectory": "/opt/my-app/templates"
  }
}
```

Absolute paths are respected as-is; relative paths are resolved against `AppContext.BaseDirectory` (not `Environment.CurrentDirectory`), so `"custom-templates"` points at `<bin>/custom-templates/`. Note that overriding the default breaks the NuGet content-file convention — installed template packages deliver into `<bin>/templates/` and won't follow your override. Only override if you're sure you don't want packaged templates.

---

## 9. Source Code Reference

| Concern | File |
|---------|------|
| Template loading & YAML parsing | [`Templates/TemplateLoader.cs`](../src/Swarmwright/Templates/TemplateLoader.cs) |
| Template & agent models | [`Templates/LoadedTemplate.cs`](../src/Swarmwright/Templates/LoadedTemplate.cs), [`Templates/AgentDefinition.cs`](../src/Swarmwright/Templates/AgentDefinition.cs) |
| Goal template expansion | [`Templates/GoalTemplateExpander.cs`](../src/Swarmwright/Templates/GoalTemplateExpander.cs) |
| Prompt assembly | [`Templates/PromptBuilder.cs`](../src/Swarmwright/Templates/PromptBuilder.cs) |
| Tool factory & resolution | [`Tools/SwarmToolFactory.cs`](../src/Swarmwright/Tools/SwarmToolFactory.cs) |
| Default tools (read/write/web_fetch) | [`Tools/DefaultToolFactory.cs`](../src/Swarmwright/Tools/DefaultToolFactory.cs) |
| Path security | [`Tools/PathSecurity.cs`](../src/Swarmwright/Tools/PathSecurity.cs) |
| Custom tool base + attributes | [`Tools/CustomToolProvider.cs`](../src/Swarmwright/Tools/CustomToolProvider.cs), [`Swarmwright.Abstractions/Tools/`](../src/Swarmwright.Abstractions/Tools/) |
| Template package config discovery | [`Extensions/ConfigurationBuilderExtensions.cs`](../src/Swarmwright/Extensions/ConfigurationBuilderExtensions.cs) |
| Dependency resolution | [`Hosting/StateMachine/StateTransitionService.cs`](../src/Swarmwright/Hosting/StateMachine/StateTransitionService.cs) (`PromoteDependentsAsync`) |
| Orchestrator lifecycle | [`Orchestration/SwarmOrchestrator.cs`](../src/Swarmwright/Orchestration/SwarmOrchestrator.cs) |
| External MCP server (observation/control) | [`Swarmwright.McpServer/Tools/SwarmMcpTools.cs`](../src/Swarmwright.McpServer/Tools/SwarmMcpTools.cs) — see [mcp-server.md](mcp-server.md) |
| Skills loading & resolution | [`Skills/FileSkillLoader.cs`](../src/Swarmwright/Skills/FileSkillLoader.cs) |
| Skills provider & tools | [`Skills/SkillsProvider.cs`](../src/Swarmwright/Skills/SkillsProvider.cs) |

---

## 10. Skills

Skills are composable markdown instruction modules that workers pull in by name. They enable reusable domain expertise without duplicating prompt content across workers.

### Worker frontmatter

Declare skills as a YAML list in the worker's frontmatter:

```yaml
---
name: architect
displayName: Cloud Architect
description: Designs cloud architectures
skills:
  - azure-architect
  - azure-cost-optimization
---

# Architect prompt body...
```

### Directory layout

Skills are resolved from two locations, template-local first:

```
templates/
  my-template/
    _template.yaml
    leader.md
    worker-architect.md          # declares skills: [azure-architect]
    skills/
      azure-architect/
        SKILL.md                 # template-local skill
        references/              # optional reference files
        scripts/                 # optional scripts (requires opt-in)
  skills/                        # shared fallback directory
    common-patterns/
      SKILL.md                   # shared across templates
```

When a worker requests skill `azure-architect`, the loader checks `<template>/skills/azure-architect/SKILL.md` first. If not found, it falls back to `templates/skills/azure-architect/SKILL.md`. First hit wins. (Skill names must be simple identifiers — no path separators or traversal sequences.)

### SKILL.md format

Each skill has a `SKILL.md` file with YAML frontmatter:

```markdown
---
name: azure-architect
description: Cloud architecture decision frameworks and Well-Architected principles.
---

# Azure Architect

## Decision Framework

1. Evaluate requirements against the five pillars
2. Apply Well-Architected Framework principles
3. Recommend architecture with tradeoff analysis
```

- `name` and `description` appear in the worker's system prompt so the model knows what's available.
- The body (below the frontmatter) is loaded on demand when the model calls `load_skill`.

The `azure-solutions-agent` template ships a full set of skills under `templates/azure-solutions-agent/skills/` (e.g. `azure-architect`, `azure-cost-optimization`, `azure-security-expert`) — use it as a worked example.

### Progressive disclosure

Workers with skills get three additional tools:

| Tool | Purpose |
|------|---------|
| `load_skill(skillName)` | Returns the full SKILL.md body for the named skill |
| `read_skill_resource(skillName, resourceName)` | Reads a file from the skill's `references/` directory |
| `run_skill_script(skillName, scriptName, arguments?)` | **v1 diagnostic only** — validates the resolved script path but does NOT execute it (see below) |

The model sees skill names and descriptions in its system prompt. It decides when to load full instructions based on the task at hand.

### Script execution (v1 status)

Script execution itself is **not implemented in v1**. Setting `allow_skill_scripts: true` in `_template.yaml` registers the `run_skill_script` tool so the model can probe that a script path exists, but invoking the tool returns a diagnostic string, not the script's output. Real in-process script execution is deferred to a future SDK-backed skills provider.

Consumers that want scripts today should either wait for the follow-up or implement them as custom tools (see [template-custom-tools.md](template-custom-tools.md)) where they control the execution path themselves.

```yaml
key: my-template
name: My Template
description: Template with script-enabled skills (v1 diagnostic only)
goal_template: "{user_input}"
allow_skill_scripts: true
```

### NuGet packaging

Skills ship alongside templates via the same `contentFiles` mechanism as the rest of the template (see [section 7](#7-shipping-a-template-as-a-nuget-package)). Because the packaging glob is `templates\**\*`, anything under `templates/<key>/skills/` is packed and copied to the consumer's output automatically — no extra MSBuild items are required.
