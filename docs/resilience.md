# Swarm Resilience

How a swarm survives two very different classes of failure — **transient** (LLM rate limits, flaky HTTP, momentary tool errors) and **strategic** (a task failed its retry budget, a dependency chain is stuck, the worker's plan needs re-thinking) — and how the server tells any consumer of the swarm (UI or external agent) what to do next.

There are two layers. They are independent and stack cleanly:

1. **[Transient resilience](#1-transient-resilience-llm-rate-limit-retry)** — two-tier retry inside the LLM call path. Keeps a single worker call alive through 429s and transient transport failures without the orchestrator ever noticing.
2. **[Recovery resilience](#2-recovery-resilience-the-recommendation-surface)** — when a task truly fails (budget exhausted, unresolvable dependency, external assumption broken), the orchestrator suspends the swarm to `AwaitingIntervention`, computes an opinion about the right recovery action, and exposes that opinion as structured state for both the admin UI and any external agent to read.

---

## 1. Transient Resilience: LLM Rate-Limit Retry

A swarm runs up to `MaxConcurrentSwarms` instances in parallel, each with multiple workers issuing LLM calls via `IChatClient.GetResponseAsync`. Under load, Azure OpenAI returns 429s. The default `AzureOpenAIClient` retries only 3 times, which is insufficient for high-concurrency swarms.

Retry is applied in two independent tiers. Tier 2 only fires when Tier 1 has been fully exhausted, so there is no double-retry amplification.

```
IChatClient call
    |
    v
[Tier 2] ResilientChatClient (Polly)
    |
    v
[Tier 1] AzureOpenAIClient (SDK built-in ClientRetryPolicy)
    |
    v
Azure OpenAI endpoint
```

### Tier 1 — SDK Built-In Retry

The `AzureOpenAIClient` is constructed with `AzureOpenAIClientOptions` specifying a `ClientRetryPolicy` with a configurable max retry count (default 6). This is the Azure SDK's native retry mechanism:

- Handles HTTP 408, 429, 500, 502, 503, 504
- Exponential backoff with full jitter
- Respects the `Retry-After` response header
- Per-request retry state (no shared state between parallel agents)

**Source:** [`ServiceCollectionExtensions.AddSwarmwrightAzureOpenAI`](../src/Swarmwright.MicrosoftAgentFramework/Extensions/ServiceCollectionExtensions.cs) (the retry policy is built by the internal `BuildClientOptions`; [`SwarmBuilderExtensions.AddSwarmOrchestration`](../src/Swarmwright/Extensions/SwarmBuilderExtensions.cs) delegates here).

### Tier 2 — Polly Safety Net

`ResilientChatClient` is an `IChatClient` decorator that wraps calls in a Polly 8.x `ResiliencePipeline`. It catches `ClientResultException` with status 429 that surfaces **after** all SDK retries are exhausted.

- Exponential backoff: `baseDelay * 2^attempt` (capped at 60s)
- Jitter: random value in `[0, baseDelay)` added to each delay
- Respects the `Retry-After` header from the exception's raw response when available
- Logs each retry at `Warning` level:
  ```
  Polly retry 1/3 for 429 Too Many Requests. Waiting 4.2s.
  ```

**Source:** [`SelfHealing/ResilientChatClient.cs`](../src/Swarmwright.MicrosoftAgentFramework/SelfHealing/ResilientChatClient.cs)

### Configuration

Retry / transport settings live under the top-level `AzureOpenAI` section (bound to `AzureOpenAIOptions` in the `Swarmwright.MicrosoftAgentFramework` package). Recovery-budget settings live under `Swarm` (bound to `SwarmOptions`):

```json
{
  "AzureOpenAI": {
    "MaxLlmRetries": 6,
    "MaxPollyRetries": 3,
    "RetryBaseDelaySeconds": 2.0
  },
  "Swarm": {
    "MaxTaskRetries": 1
  }
}
```

| Setting | Section | Default | Description |
|---------|---------|---------|-------------|
| `MaxLlmRetries` | `AzureOpenAI` | `6` | Tier 1: max retries in the Azure SDK `ClientRetryPolicy`. |
| `MaxPollyRetries` | `AzureOpenAI` | `3` | Tier 2: max retries in the Polly `ResiliencePipeline`. |
| `RetryBaseDelaySeconds` | `AzureOpenAI` | `2.0` | Tier 2: base delay for exponential backoff (seconds). |
| `MaxTaskRetries` | `Swarm` | `1` | Layer 2 (recovery): per-task Continue retry cap. See [§ 2](#2-recovery-resilience-the-recommendation-surface). |

> `AzureOpenAIOptions` also exposes `NetworkTimeoutSeconds` (default 600) and `UseBackgroundResponses`; those govern transport timeout and the Responses-API background mode rather than retry, and are documented alongside the option class.

### Worst-Case Latency

With defaults, a single LLM call can retry up to 9 times total (6 SDK + 3 Polly) before failing. Assuming `Retry-After` headers are not present:

| Tier | Retries | Approximate Cumulative Delay |
|------|---------|------------------------------|
| Tier 1 (SDK) | 6 | ~63s (SDK-managed backoff) |
| Tier 2 (Polly) | 3 | ~14–18s (2s + 4s + 8s + jitter) |
| **Total** | **9** | **~77–81s** |

If the endpoint returns `Retry-After` headers, both tiers respect them and delays will be longer.

### Tuning for Your Workload

- **More concurrent swarms:** increase `MaxLlmRetries` and `RetryBaseDelaySeconds` to give the endpoint more time to recover between bursts.
- **Lower latency tolerance:** decrease `MaxPollyRetries` or `MaxLlmRetries` so failed calls surface faster.
- **Dedicated Azure OpenAI deployment:** with higher TPM (tokens per minute) quotas, you may reduce retry counts since 429s will be less frequent.

### Streaming Support

`ResilientChatClient` also wraps `GetStreamingResponseAsync`. Retry is applied during stream initialization (the first `MoveNextAsync` call). Once the stream begins yielding tokens, it is not retried mid-stream — a 429 during streaming is a connection-level failure that the SDK handles at Tier 1.

---

## 2. Recovery Resilience: The Recommendation Surface

When transient retries give up and a task lands in `Failed`, or when the dependency chain is stuck, or when a failure crosses a retry-budget boundary, the swarm cannot self-heal — a human or an arbiter agent has to decide. The server's job is to make that decision **as informed and as consistent across consumers as possible**.

### Why this matters (design vision)

A swarm has four recovery actions: **Continue**, **Smart Continue**, **Force Synthesis**, **Cancel**. In a naive design the admin UI would show all four buttons indiscriminately and the operator would guess. That mental model ("which one is right?") is muddy enough to produce ghost-fix changes where a clever-looking edit silently skips LLM invocation.

The recommendation surface says: the server is the system of record; the human and any external AI agent are **peer consumers** riding a shared tool surface (HTTP + MCP). The server computes its best opinion about which action is appropriate given the current state, and exposes that opinion as structured state. Both consumers read the same hint, and neither has to re-implement the routing logic.

- **Capability parity over gatekeeping.** The recommendation is an *opinion*, not a gate. `validActions` still enumerates the full menu. An external agent can override if it has better context.
- **Pure function, recomputed each read.** Canonical state (the `swarms` and `swarm_tasks` tables) stays the single source of truth; no cache drift. A deterministic provider ships today; an LLM-driven provider can drop in behind the same interface if a case proves genuinely judgment-requiring.
- **Clarify the mental model once, everywhere.** The table below is the canonical reference.

### The four recovery actions

| Action | What it actually does | When to pick it |
|---|---|---|
| **Continue** | Deterministic workflow resume. The orchestrator re-spawns the existing worker agents for still-open tasks. Workers ARE invoked (they ARE LLMs). What is NOT invoked is the leader doing any re-planning. Failed tasks with retry budget get flipped back to Pending (consuming one budget point) and retried; Pending tasks get picked up; orphan InProgress tasks are reset without consuming budget. | State is resumable as-is: failed tasks with retry budget, viable Pending work, host-failure restarts. |
| **Smart Continue** | Invoke the **leader** LLM with full swarm context (tasks, inboxes, transition history) and let it reason about the repair — reset / add / abandon tasks — before workers resume. | The state is tangled and deterministic rules can't unstick it: all failures retry-exhausted, dependency chain dead, leader needs to adapt the plan. |
| **Force Synthesis** | Abandon remaining work. Run the synthesis agent against whatever is Completed. Terminal-ish. | Nothing left to rescue. Only Completed tasks remain; accept partial results. |
| **Cancel** | Terminal. No synthesis. | Kill the run. |

### The recommendation state shape

`GET /api/swarm/{id}` (and its MCP equivalent) returns the full swarm metadata **plus** a `recommendation` object when the swarm is in an actionable non-terminal state (`AwaitingIntervention` or `NeedsDiagnosis`). Otherwise `recommendation` is `null`.

```json
{
  "swarmId": "7e1c8d95-152c-425f-9ab8-73002b34a6e5",
  "phase": "AwaitingIntervention",
  "state": "AwaitingIntervention",
  "isRunning": false,
  "recommendation": {
    "validActions": ["continue", "smart-continue", "force-synthesis", "cancel"],
    "recommendedAction": "continue",
    "rationale": "No failures. 1 Pending task(s) viable. Continue resumes the workflow."
  }
}
```

See [SwarmMetadataResponse.cs](../src/Swarmwright/Database/Models/SwarmMetadataResponse.cs). The JSON shape is a frozen contract (field order and casing are contractually significant) so frontend hydration and MCP consumers don't silently break when a field is added. The `GET /api/swarm/{id}` endpoint that serves it lives in [SwarmEndpointExtensions.cs](../src/Swarmwright.AspNetCore/Extensions/SwarmEndpointExtensions.cs).

### Rule table (deterministic provider v1)

[DeterministicSwarmContinueProvider.cs](../src/Swarmwright/Recommendation/DeterministicSwarmContinueProvider.cs) is a pure function over `(swarm.State, tasks, maxTaskRetries)`. Rules are evaluated top-down:

| Precondition | `recommendedAction` | Rationale template |
|---|---|---|
| swarm state is not `AwaitingIntervention` or `NeedsDiagnosis` | `null` (no recommendation) | — |
| no Pending, no Blocked, no Failed, **no InProgress** | `force-synthesis` | No open work and no rescuable failures; Force Synthesis produces the report from Completed tasks. |
| failed tasks exist, all with retry budget | `continue` | N failed task(s) with retry budget remaining; Continue will retry them. |
| failed tasks exist, all retry-exhausted | `smart-continue` | N failed task(s), all retry budget exhausted. Smart Continue required — leader must reset or abandon. |
| failed tasks exist, mixed budget | `continue` | M of N failed tasks have retry budget; Continue will retry those. Remaining failures need Smart Continue on a second pass. |
| no failed, no Pending, **InProgress exists** (orphan) | `continue` | N orphan InProgress task(s) detected (no live worker — typically a crashed run). Continue will reset and retry without consuming retry budget. |
| no failed, Pending exists | `continue` | No failures. N Pending task(s) viable. Continue resumes the workflow. |
| no failed, no Pending, only Blocked | `smart-continue` | Dependency chain stuck with N Blocked task(s) and no viable Pending; Smart Continue to unblock via leader. |

Adding a new case is a private method in the provider plus one unit test in [DeterministicSwarmContinueProviderTests.cs](../tests/Swarmwright.Tests/Recommendation/DeterministicSwarmContinueProviderTests.cs).

### Extension seam: LLM-driven recommendations

The provider lives behind `IRecommendedSwarmContinueProvider`. A future `LlmSwarmContinueProvider` can drop in without touching the endpoint, the DTO, or the UI. Deferred until a case proves genuinely judgment-requiring — today's rule table is legible, cheap, and good enough.

### Consumer expectations

- **Admin UI** reads `recommendation.recommendedAction` and highlights the matching button. `rationale` surfaces as a tooltip. All four buttons remain visible — capability parity.
- **External agents via MCP** read the same field through the MCP tool response (`get_swarm`), and drive recovery through `continue_swarm`, `smart_continue_swarm`, `force_synthesis_swarm`, and `cancel_swarm`. Their reasoning loop treats `recommendedAction` as the prior and may still override when they have specific context.

---

## 3. Handler Invariants

Recovery resilience depends on two handler behaviours that are easy to regress. Both have unit regression tests in [SwarmInterventionHandlerTests.cs](../tests/Swarmwright.Tests/Extensions/SwarmInterventionHandlerTests.cs) and [SwarmInterventionHandlerSmartContinueTests.cs](../tests/Swarmwright.Tests/Extensions/SwarmInterventionHandlerSmartContinueTests.cs).

### Continue accepts viable work even without failures

Continue is a deterministic resume — it needs *something* to run. A brittle version rejects with 409 `no_retry_budget` whenever no Failed task has retry budget, even when a Pending task is sitting there waiting for a worker. That makes a hung swarm (architect + security Completed, cost-expert Pending, nothing failed) impossible to resume — Continue rejects spuriously.

The handler ([SwarmInterventionHandler.cs](../src/Swarmwright/Extensions/SwarmInterventionHandler.cs)) accepts when **any** of three conditions is true:

- at least one Failed task has retry budget (flip it Failed → Pending, bump `retry_count`), OR
- at least one task is already Pending (let the orchestrator pick it up on resume), OR
- at least one orphan InProgress task exists (reset it Failed→Pending style back to Pending **without** consuming retry budget — a crash-cleanup reset, recorded under the `orphan_resume` reason).

If none of the three exists, reject with `no_retry_budget` — the caller should Smart-Continue or Force-Synthesis. (A swarm already in `NeedsDiagnosis` is likewise rejected from Continue; Smart Continue is required.)

### Smart Continue short-circuits when there are no failures

A brittle Smart Continue always invokes the leader advisor. If the swarm has zero failed tasks the advisor has nothing to repair, returns `null`, and the handler folds to a 409 `repair_failed` with "Leader did not produce a repair plan." In the UI this looks like Smart Continue is broken.

The handler checks `failedTasks.Count == 0` up front and, when there is open work (`hasOpenWork` — any Pending or Blocked task), **skips the advisor** and transitions the swarm directly to Executing with reason `user_smart_continue_no_failures` (see [TransitionReasons.cs](../src/Swarmwright/Hosting/StateMachine/TransitionReasons.cs)). The audit trail records that a human pressed Smart Continue against a no-failure swarm intentionally. Honoring the override (rather than rejecting) keeps capability parity with an external agent driving the surface.

---

## 4. The Resume Preservation Guard

A class of bug that sits one layer below the recovery actions: a swarm goes into `AwaitingIntervention`, is rehydrated from the DB, and then crashes in the orchestrator's round loop with `InvalidStateTransitionException: Completed -> InProgress`.

**The trap.** [SwarmService.AddTaskAsync](../src/Swarmwright/Services/SwarmService.cs) derives a task's initial `Status` from its `BlockedBy` count. The naive version overwrites `Status` unconditionally:

```csharp
// NAIVE (the trap)
task.Status = task.BlockedBy.Count > 0
    ? TaskState.Blocked
    : TaskState.Pending;
```

The planner relies on this heuristic — a freshly-planned `SwarmTask` has `Status = Pending` (the enum default) and the heuristic demotes it to `Blocked` if it has deps. But `SwarmService.LoadAsync` also calls `AddTaskAsync` on every DB-persisted task during a resume. For a Completed or Failed task with empty `BlockedBy`, the naive heuristic would silently reset `Status = Pending`. The orchestrator would then treat those tasks as runnable, try to transition them to `InProgress`, and the state-machine guard rejects `Completed → InProgress`.

**The guard.** Distinguish planning states from terminal/active states, and only re-derive the former:

```csharp
var isTerminalOrActive = task.Status is TaskState.Completed
    or TaskState.Failed
    or TaskState.InProgress
    or TaskState.AwaitingFeedback;

if (!isTerminalOrActive)
{
    task.Status = task.BlockedBy.Count > 0
        ? TaskState.Blocked
        : TaskState.Pending;
}
```

Planning states (`Pending`, `Blocked`) still get re-derived from deps; terminal/active states are preserved as-is. `AddTaskAsync` behaviour — the Blocked-vs-Pending derivation and persistence — is covered in [SwarmServiceTests.cs](../tests/Swarmwright.Tests/Services/SwarmServiceTests.cs).

As a belt-and-suspenders safeguard, [SwarmOrchestrator.ExecuteAsync](../src/Swarmwright/Orchestration/SwarmOrchestrator.cs) also filters `tasksToRun` to `Status == TaskState.Pending` immediately before writing the `InProgress` transition — so even if a future regression re-introduces the same class of bug upstream, the round loop won't try to promote a non-Pending task.

---

## Related Components

| Component | Path | Role |
|-----------|------|------|
| `ResilientChatClient` | `src/Swarmwright.MicrosoftAgentFramework/SelfHealing/ResilientChatClient.cs` | Tier 2 Polly retry decorator (§ 1) |
| `ServiceCollectionExtensions.AddSwarmwrightAzureOpenAI` | `src/Swarmwright.MicrosoftAgentFramework/Extensions/ServiceCollectionExtensions.cs` | Tier 1 SDK retry wiring (§ 1); `AddSwarmOrchestration` delegates here |
| `AzureOpenAIOptions` | `src/Swarmwright.MicrosoftAgentFramework/Configuration/AzureOpenAIOptions.cs` | Configuration binding for tier 1 + tier 2 retry knobs (§ 1) |
| `SwarmOptions` | `src/Swarmwright/Configuration/SwarmOptions.cs` | Configuration binding for recovery-budget settings (§ 2) |
| `CircuitBreaker` | `src/Swarmwright/SelfHealing/CircuitBreaker.cs` | Tool-level failure tracking (separate concern) |
| `SwarmContinueRecommendation` | `src/Swarmwright/Recommendation/SwarmContinueRecommendation.cs` | Recommendation record (§ 2) |
| `IRecommendedSwarmContinueProvider` | `src/Swarmwright/Recommendation/IRecommendedSwarmContinueProvider.cs` | Provider interface + future LLM seam (§ 2) |
| `DeterministicSwarmContinueProvider` | `src/Swarmwright/Recommendation/DeterministicSwarmContinueProvider.cs` | v1 rule-table implementation (§ 2) |
| `SwarmMetadataResponse` | `src/Swarmwright/Database/Models/SwarmMetadataResponse.cs` | Frozen JSON contract that carries the recommendation (§ 2) |
| `SwarmInterventionHandler` | `src/Swarmwright/Extensions/SwarmInterventionHandler.cs` | Continue / Smart Continue / Skip (Force Synthesis) / Cancel handler, with short-circuit + viable-work acceptance (§ 3) |
| `TransitionReasons` | `src/Swarmwright/Hosting/StateMachine/TransitionReasons.cs` | Canonical audit-row reason strings including `user_smart_continue_no_failures` (§ 3) |
| `SwarmService` | `src/Swarmwright/Services/SwarmService.cs` | Resume-preservation guard in `AddTaskAsync` (§ 4) |

## Cross-references

- [state-machine.md](state-machine.md) — write architecture; recovery transitions go through the state-transition service.
- [state-swarm-instances.md](state-swarm-instances.md) — `AwaitingIntervention` / `NeedsDiagnosis` state definitions and every recovery transition.
- [state-task.md](state-task.md) — task-level retry semantics (`retryCountDelta`) and the Failed → Pending paths.
- [mcp-server.md](mcp-server.md) — the MCP tool surface (`continue_swarm`, `smart_continue_swarm`, `force_synthesis_swarm`, `cancel_swarm`).
- [admin.md](admin.md) — how the admin UI consumes `recommendation.recommendedAction`.
