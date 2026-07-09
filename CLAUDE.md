# Swarmwright — repository guide

A **framework-agnostic multi-agent swarm toolkit** for .NET 10 / C#. Swarmwright provides the
generic coordination layer for agent *swarms* — supervisor/worker orchestration, fan-out +
aggregate (ensembles, voting, cross-verification), and continuous background swarms (queue-drained
workers for reconciliation/compaction/enrichment) — independent of any one agent framework.

**Status: scaffolding.** The first body of work is a **port**: the swarm originated as Copilot
swarm work, then a **CSAT-specific C# implementation** living at
`/mnt/c/dev/other/ai-agents/server-agent/src` (Windows `C:\dev\other\ai-agents\server-agent\src`).
The plan mirrors the AgentMemoryOS playbook: **strip the CSAT-coupled bits, keep the
framework-agnostic core, repackage for NuGet.** First real step: read that source and map reusable
vs CSAT-coupled before designing the public surface.

## Solution layout (planned — firms up after the port)

- `src/Swarmwright.Abstractions` — swarm/agent interfaces + DTOs. No logic.
- `src/Swarmwright` — orchestration engine, swarm primitives, the background pipeline, DI.
- `src/Swarmwright.*` — per-framework adapter packages (e.g. Microsoft Agent Framework), kept
  optional so the core stays framework-agnostic.
- `tests/Swarmwright.Tests` — unit tests (no external services).

Treat this as provisional; the real shape is decided once the existing code is mapped.

## Build & test

```bash
dotnet build      # warnings are errors; must be clean
dotnet test       # MSTest
```

## Quality gates (non-negotiable)

Write all C# via the `csharp-quality-developer` workflow. The build enforces
`TreatWarningsAsErrors`, `AnalysisMode=all`, `EnforceCodeStyleInBuild`,
`GenerateDocumentationFile`, and full StyleCop (config in `src/.editorconfig`,
`tests/.editorconfig`, `stylecop.json`).

- `.cs` files: CRLF line endings, UTF-8 **with BOM**, 4-space indent, single final newline,
  no trailing whitespace.
- `this.` qualification on members; private fields camelCase (no underscore); `sealed` where
  applicable; file-scoped namespaces; usings outside namespace, System first.
- Every public type/member in `src/` needs XML documentation. Docs are relaxed in `tests/`.
- No file headers (SA1633 / IDE0073 are disabled).

## Dependency management

- **Centralized Package Management**: all versions live in `Directory.Packages.props`
  (currently empty save the StyleCop analyzer). `<PackageReference>` entries in `.csproj` carry
  **no** `Version`. StyleCop is applied globally via `<GlobalPackageReference>`.
- Test projects use the **MSTest SDK** pinned in `global.json` (`<Project Sdk="MSTest.Sdk">`).
  Pass test args after a `--` separator, e.g. `dotnet test -- --filter "…"`.
- `src/` and `tests/` each have their own `Directory.Build.props` (TFM, analyzers, nullable,
  packaging); do not duplicate those settings in individual `.csproj` files.

## Reference source

Third-party source (the agent framework(s) Swarmwright adapts to, Microsoft.Extensions.*) is
cloned under the git-ignored `research/` folder — read it to confirm real APIs rather than
guessing. Do not decompile NuGet packages when a public repo exists.
