# Azure Solutions Agent — Swarm Template

`templateKey: azure-solutions-agent`

A deep-research **Azure solutions architecture** team. Given a goal, it designs a cloud architecture,
gates it through cost review, then plans and writes deployable Infrastructure-as-Code (Bicep or Terraform).
Every specialist is backed by the **learn.microsoft.com** MCP server (`microsoft_docs_search` /
`microsoft_docs_fetch`) so claims and service choices cite current Microsoft documentation.

This is the **heaviest** of the shipped templates. For lighter, faster research runs prefer
`microsoft-deep-research` (Microsoft-ecosystem, learn.microsoft.com-backed) or `deep-research` (general).

## What it produces

- An architecture design (`architecture-design.md`) — service selection with trade-off rationale, topology.
- A security/identity + compliance design (`security-design.md`).
- A cost review with an explicit **approval gate** (and cost-saving revision requests).
- An IaC module plan and **deployable `.bicep` / `.tf` files** written to the work directory.
- A synthesized final report.

Infrastructure language defaults to **Bicep** unless the goal specifies Terraform.

## Agent inventory

**Leader** — Azure Solutions Architect Lead. Interviews the user (see Behavior below), then plans the run as
four phases with explicit task dependencies.

**Workers** (each draws on role-specific Azure skills + the learn.microsoft.com MCP endpoint):

| Worker | Role | Phase |
|--------|------|-------|
| `architect` | Cloud architecture design | 1 — Design |
| `security` | Identity, security posture, compliance | 1 — Design |
| `ai-ml` | AI/ML services design — **conditional**: skipped entirely when the goal has no AI/ML component | 1 — Design |
| `cost-expert` | Cost-optimization review; **must approve** before the run proceeds | 2 — Cost gate |
| `iac-architect` | Breaks approved designs into standardized IaC modules (naming, shared variables, dependency map) | 3 — IaC planning |
| `iac-developer` | Writes Bicep/Terraform modules — **one task per module**, run in parallel | 4 — IaC implementation |

**Skills shipped** (`templates/azure-solutions-agent/skills/`): `azure-architect`, `azure-solutions-expert`,
`azure-engineer`, `azure-developer`, `azure-network-engineer`, `azure-kubernetes-expert`, `azure-ml-engineer`,
`azure-ai-expert`, `azure-security-expert`, `azure-cost-optimization`, `entra-expert`.

## Phase flow & dependencies

```
Phase 1  DESIGN (parallel)      architect ‖ security ‖ ai-ml*      (no inter-dependencies)
Phase 2  COST GATE              cost-expert            (BLOCKED BY all design tasks; must approve)
Phase 3  IAC PLANNING           iac-architect          (BLOCKED BY cost-expert)
Phase 4  IAC IMPLEMENTATION     iac-developer × N      (BLOCKED BY iac-architect; one task per module)
            * ai-ml is omitted when the solution has no AI/ML workload
```

> **Configuration requirement:** `Swarm:MaxRounds` must be **≥ 8** for this template (4 phases + buffer).
> The default is 8; lowering it will starve later phases.

## Behavior — interactive vs. headless (important)

The leader defines a **Q&A Interview Phase**: *"Before creating a plan, you MUST interview the user… ask the
4–6 most relevant questions one or two at a time… then call `begin_swarm` with a refined goal."* The refined
goal right-sizes the solution (e.g. "mid-size AKS deployment for 12 apps with pragmatic security" rather than
"enterprise container platform").

How that plays out depends on **how the swarm is created**:

- **Interactive (Swarm Admin SPA / chat / refinement channel):** the leader runs the interview, the user
  answers, and the leader calls `begin_swarm` with the refined goal. The persisted swarm has
  `qaRefinedGoal` set, and the team is sized to the answers.
- **Headless (`POST /api/swarm`, the MCP `create_swarm` tool, or `SwarmExecutor`):** there is **no interactive
  channel for the interview**, so it is **skipped** — the swarm proceeds on the **raw goal as submitted**
  (`qaRefinedGoal` is not set) and moves straight to Planning → Executing.

**Implication:** when you submit this template **headless**, give it a **well-scoped goal** (constraints,
scale, security expectations, Bicep vs. Terraform). There is no interview to narrow an under-specified goal,
so vague input tends to over-engineer.

## Cost note

A full run is 6 specialists across 4 phases with cost-gating and IaC generation — it is **minutes-long and
token-heavy** on your Azure OpenAI deployment. Use it when you actually want an architecture + IaC artifact;
for exploration or Q&A, reach for a research template instead.
