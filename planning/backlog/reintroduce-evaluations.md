# Backlog: Re-introduce the evaluation subsystem (LLM-as-judge + guardrails + sampling)

**Status:** Backlog (not started)
**Captured:** 2026-06-07
**Depends on:** Stage 10 (Swarmwright.Workflows) — the eval tooling is wired through the workflow
executor base, so it lands with or after the workflows port.

## Context — why this is a backlog item, not part of the initial port

The initial Swarmwright port deliberately deferred the **evaluation subsystem**. It was enterprise
tooling welded to the CSAT *Workflows* layer (`CSAT.IT.AI.Agent.Workflows`, which we are not porting
wholesale) and to the CSAT custom agent/eval projects. When the Workflows bridge was scoped, the plan
said to "drop `ExecutorGuardRailsBase` (CSAT) — reimplement as a thin MAF `Executor` wrapper (port
only the eval/guardrail bits we actually need)." This backlog captures the **full** capability so we
can re-introduce it intentionally rather than lose it.

This is high-value enterprise tooling worth recapturing: automated quality/safety scoring of agent
output, deterministic guardrails that can block bad output inline, **sampling** to control evaluation
cost at scale, durable result storage, and reporting.

## What the CSAT evaluation subsystem provides (ground truth)

Source in the corpus (read these before implementing — do not guess the API):

- `research/csat-server-agent/src/CSAT.IT.AI.Agent.Workflows/Executors/ExecutorGuardRailsBase.cs`
  — abstract `Executor<TInput, TOutput>` (MAF) base that orchestrates two evaluation tiers per run.
- `research/csat-server-agent/src/CSAT.IT.AI.Agent.Workflows/Executors/EvaluationSampler.cs`
  — the sampling gate.
- `research/csat-server-agent/src/CSAT.IT.AI.Agent.Workflows/Executors/WorkflowEvaluationExecutor.cs`
  — workflow-level evaluation executor.
- `research/csat-server-agent/src/CSAT.IT.AI.Agent.Workflows/Notifications/EvaluationNotification.cs`
  + `EvaluationNotificationConsumer.cs` — async background dispatch of evaluation work (Mediate today).
- `research/csat-server-agent/src/CSAT.IT.AI.Agent.Evaluation/` — storage + configuration:
  `Storage/InMemoryResultStore.cs`, `Factories/EvaluationStorageFactory.cs`,
  `Configuration/{EvaluationConfiguration,ExecutorEvaluationConfig,EvaluationStorageConfiguration,FabricOneLakeConfiguration}.cs`.

### Two evaluation tiers (in `ExecutorGuardRailsBase`)

1. **Synchronous guardrails** — `IValidator<TOutput, TOutput>` (`GetSynchronousValidators()`).
   Deterministic, run inline on every output. With `ExecutorEvaluationConfig.BlockOnFailure = true`
   they can **block/replace** a bad output before it propagates. Gated by
   `EnableSynchronousValidation`.
2. **Asynchronous LLM-as-judge evaluation** — `IEvaluator` (`GetAsynchronousEvaluators()`), built on
   `Microsoft.Extensions.AI.Evaluation`. **Sampled** (see below), run **off the request path** via an
   `EvaluationNotification` → consumer → storage. Gated by `EnableAsynchronousEvaluation`. Scores are
   compared against per-metric `Thresholds`.

Evaluators observed in use (M.E.AI.Evaluation Quality + Safety):
Coherence, Completeness, Fluency, Groundedness, Relevance, TaskAdherence, ToolCallAccuracy
(Quality); ContentSafety, CodeVulnerability (Safety); plus composite per-executor evaluators
(planner/validator/enhancer/formatter/aggregator/workflow).

### Sampling (the bit to preserve — `EvaluationSampler.ShouldSample`)

```csharp
public static bool ShouldSample(double? perExecutorRate, double globalRate, Func<double> draw)
{
    var rate = perExecutorRate ?? globalRate;   // per-executor overrides global
    if (rate >= 1.0) return true;               // always
    if (rate <= 0.0) return false;              // never
    return draw() < rate;                        // random draw in [0,1)
}
```

- **Global** default rate: `EvaluationConfiguration.SamplingRate` (default `1.0`).
- **Per-executor** override: config key `Workflows:{Executor}:SamplingRate`.
- `draw` is injected (a `Func<double>` in `[0,1)`) so sampling is deterministic in tests.

This keeps async LLM-judge cost bounded at production scale — e.g. evaluate 5% of runs globally but
100% of a risky executor.

### Configuration surface

- `EvaluationConfiguration`: `Enabled` (true), `SamplingRate` (1.0), `Endpoint`, `DeploymentName`,
  `Temperature` (0.3 for the judge model), `Storage`.
- `ExecutorEvaluationConfig`: `EnableSynchronousValidation` (true), `EnableAsynchronousEvaluation`
  (true), `Thresholds` (per-metric `Dictionary<string,double>`), `BlockOnFailure` (false).
- `EvaluationStorageConfiguration`: `ReportStorageType` (`Memory` | `File` | `FabricOneLake`),
  `FileStoragePath`, `FabricOneLake`, `EnableResponseCaching` (true), `CacheTTL` (7 days),
  `ExecutionName`, `Tags`.

### Storage / reporting

- Result stores: in-memory, file, and **Fabric OneLake** (`Azure.Storage.Files.DataLake`), behind
  `EvaluationStorageFactory`. Response caching (7-day TTL) avoids re-paying for identical judge calls.
- Reporting via `Microsoft.Extensions.AI.Evaluation.Reporting` / `.Reporting.Azure` (execution name +
  tags identify a run set).

## Target design when re-introduced in Swarmwright (framework-agnostic)

Keep the core framework-agnostic; put MAF-specific executor glue in the workflows/adapter packages.

1. **`Swarmwright.Abstractions` (or a new `Swarmwright.Evaluation.Abstractions`)**: seam interfaces —
   `IEvaluationSampler`, `ISwarmEvaluator`/reuse `IEvaluator`, `IEvaluationResultStore`,
   `IEvaluationPublisher` (reuse the **existing channel notification pipeline**, not Mediate — see
   `Swarmwright.Events.ISwarmNotificationPublisher`/`SwarmNotificationBackgroundService`). The async
   evaluation tier should publish an `EvaluationRequested` notification and be drained off-thread by
   the same background pipeline that already replaced Mediate.
2. **`Swarmwright.Evaluation`** (new core-side package): `EvaluationSampler` (port verbatim — it is
   pure and framework-agnostic), the M.E.AI.Evaluation wiring (Quality + Safety evaluators), the
   storage factory + InMemory/File stores, and the configuration classes above.
3. **`Swarmwright.Evaluation.FabricOneLake`** (optional adapter): the OneLake store, kept optional so
   the core stays dependency-light (mirrors how archival keeps Azure Blob optional).
4. **`Swarmwright.Workflows`**: reintroduce the guardrails base as a thin `ExecutorGuardRailsBase`
   over MAF's `Executor<TInput,TOutput>` that composes the sampler + sync validators + async
   evaluators. This is where the dropped CSAT base is re-derived.
5. **Swarm-native hook (beyond workflows)**: consider evaluating **worker task outputs** directly in
   the orchestrator (per `TaskOutputEntry`), not only workflow executors — sampling per worker role
   via `Swarm:{Role}:SamplingRate`. This generalizes the per-executor rate to the swarm domain.

## Dependencies (already pinned centrally)

`Directory.Packages.props` already pins the eval stack — no new version decisions needed:
`Microsoft.Extensions.AI.Evaluation` 10.4.0, `.Quality` 10.4.0, `.Safety` 10.3.0-preview.1.26109.11,
`.Reporting` 10.4.0, `.Reporting.Azure` 10.4.0. (They are pinned but currently unreferenced by any
project — verify they're still required when this work starts.) Fabric OneLake needs
`Azure.Storage.Files.DataLake`.

## Suggested phasing

1. Port `EvaluationSampler` + config classes + InMemory/File stores into `Swarmwright.Evaluation`
   (+ unit tests for the sampling gate, including the injected-`draw` determinism).
2. Wire the async evaluation tier onto the existing channel notification pipeline (publish
   `EvaluationRequested`, handle in a background `ISwarmNotificationHandler`).
3. Re-derive `ExecutorGuardRailsBase` in `Swarmwright.Workflows` (sync validators + sampled async
   evaluators); land with the Workflows stage.
4. Add the M.E.AI.Evaluation Quality + Safety evaluators behind `GetAsynchronousEvaluators()`.
5. Add the optional `Swarmwright.Evaluation.FabricOneLake` store + reporting.
6. (Stretch) Swarm-native per-worker-output evaluation with per-role sampling.

## Open questions

- **Judge model**: reuse the swarm's `IChatClient`, or a dedicated cheaper judge `IChatClient`
  (Temperature 0.3)? CSAT used a separately-configured Endpoint/DeploymentName.
- **Sampling scope**: per-workflow-executor only (CSAT), or extend to per-swarm and per-worker-role?
- **Storage default**: ship InMemory + File in core; keep OneLake + Azure reporting as optional
  adapters (recommended, mirrors archival).
- **Guardrail blocking**: do we want `BlockOnFailure` for swarm worker outputs, or evaluation-only
  (observe) initially?
