# Swarm Run Archival

Promote a completed swarm run's working directory to **durable Azure Blob storage**, so a rich per-agent corpus
(transcripts, prompts, the synthesis report, the agent-attributed output index) survives the host-local
`WorkBasePath` being wiped on a recycle or node move. It is a **generic, opt-in platform feature** — it retains
*any* swarm run (research, IaC, code-review), not just one consumer's.

**Off by default.** A host turns it on per-environment; nothing is written anywhere until you do.

For app setup and the `Swarm:*` config schema, see [swarm.md](swarm.md). For the operator view, see
[admin.md](admin.md).

---

## 1. What gets archived

The **whole** `{WorkBasePath}/{swarmId}/` tree, verbatim:

```
{swarmId}/
  .chat/
    {agent}.jsonl          per-agent conversation transcript
    {agent}.system.md      per-agent system prompt
    agents.json            roster: [{ name, role, displayName }]
    synthesis.jsonl        the leader's synthesis conversation (on success)
    synthesis.system.md    the synthesis system prompt (on success)
  synthesis-report.md      the final deliverable (on success)
  task-outputs.json        agent-attributed output index (see §6)
  ...                      any files agents wrote via the write tool
  manifest.json            run metadata — written LAST (see §5)
```

Archival runs on **every terminal state — Complete, Cancelled, and Failed.** Failed runs are often the most
valuable to keep, so they are never skipped.

---

## 2. Enabling it

Add a `Swarm:Archival` block. `Enabled` is the only toggle in v1; the sink is implicitly Azure Blob.

```jsonc
"Swarm": {
  "WorkBasePath": "./swarm-workdirs",
  "Archival": {
    "Enabled": true,                                                  // off by default
    "ContainerUri": "https://<acct>.blob.core.windows.net/swarm-corpus",
    "CredentialType": null,        // null = environment default (see §3); or override explicitly
    "ManagedIdentityClientId": "<mi-guid>",                           // user-assigned MI (prod)
    "TenantId": "...", "ClientId": "...", "ClientSecret": "..."       // service principal (dev/test)
  }
}
```

`ContainerUri` binds to a `System.Uri`; `CredentialType` binds to the `SwarmArchivalCredentialType` enum (see §3).
These are the exact properties on `SwarmArchivalOptions`
(`../src/Swarmwright/Configuration/SwarmArchivalOptions.cs`).

Local artifacts are **always retained** — archival is a copy, never a move. Local cleanup stays with the existing
work-directory lifecycle; this feature deletes nothing.

> **Do not** point `WorkBasePath` at a blobfuse mount. The orchestrator and agents do active small-file I/O
> (create/rename/concurrent writes) where blobfuse's weak POSIX semantics and latency bite. Live work stays on the
> local/ephemeral path; archival is a **post-run copy**.

---

## 3. Credentials (dev vs prod)

The credential is chosen by **hosting environment**, resolved by `SwarmArchiverCredentialFactory`
(`../src/Swarmwright/Archival/SwarmArchiverCredentialFactory.cs`):

| Environment | Credential | Config used |
|---|---|---|
| `Development` / `Testing` | `ClientSecretCredential` | `TenantId` + `ClientId` + `ClientSecret` |
| anything else (prod) | `ManagedIdentityCredential` | `ManagedIdentityClientId` (user-assigned), else system-assigned |

Set `CredentialType` explicitly to override the environment default — e.g. to use a managed identity from a dev
box, or a service principal in a non-prod cloud environment. The `SwarmArchivalCredentialType` enum maps each value
directly onto an `Azure.Identity` credential:

| `CredentialType` | Credential constructed |
|---|---|
| `Default` | `DefaultAzureCredential` (chained discovery) |
| `ManagedIdentity` | `ManagedIdentityCredential` (system- or user-assigned via `ManagedIdentityClientId`) |
| `ClientSecret` | `ClientSecretCredential` (`TenantId` + `ClientId` + `ClientSecret`) |
| `ClientCertificate` | `ClientCertificateCredential` |
| `Environment` | `EnvironmentCredential` (credentials from environment variables) |

---

## 4. What happens if blob storage isn't configured

Archival **fails safe** — it never crashes the host and never affects a swarm run:

| Situation | Behavior |
|---|---|
| `Enabled = false` (default) | A no-op archiver (`NoOpSwarmRunArchiver`) is registered. The run still publishes its completion notification and the consumer still runs, but it just logs a `Debug` skip and uploads nothing. |
| `Enabled = true` but `ContainerUri` missing/null | **Same no-op fallback** — the host does not throw at startup or at run end. |
| `Enabled = true` + valid `ContainerUri` | Blob archival runs (best-effort — see §5). |

Registration lives in `AddSwarmRunArchival`
(`../src/Swarmwright/Extensions/SwarmServiceExtensions.cs`): the `BlobSwarmRunArchiver` is only wired when
`Enabled` **and** `ContainerUri is not null`; every other path binds `NoOpSwarmRunArchiver`.

The `task-outputs.json` index (§6) is written by the orchestrator **independently of archival**, so it is present
in the local work directory even when archival is off.

> ⚠️ **Misconfiguration is quiet.** The no-op skip logs at `Debug` as "archival disabled". If you set
> `Enabled = true`, see no archives, and your log level is above `Debug`, the most likely cause is a missing
> `ContainerUri` — it silently took the no-op path. Check `ContainerUri` first.

---

## 5. Execution model — non-blocking & best-effort

Archival must never slow a run down or fail it. It rides the swarm's **in-process notification pipeline**, not a
bespoke queue:

- At each run's terminal point the dispatcher publishes a **`SwarmRunCompletedNotification`** through
  `ISwarmNotificationPublisher`. The default publisher (`ChannelSwarmNotificationPublisher`) enqueues the
  notification onto a bounded in-process channel and returns immediately — **run completion is not delayed.**
  Publishing is itself best-effort: a missing publisher or an enqueue failure is logged and swallowed on the
  terminal path.
- A background **`SwarmNotificationBackgroundService`** drains the channel off-thread and dispatches each
  notification, in a fresh DI scope, to its registered `ISwarmNotificationHandler<T>` handlers.
- **`SwarmRunCompletedNotificationConsumer`** is that handler: it builds a `SwarmRunArchiveContext` and delegates
  the copy to `ISwarmRunArchiver`, wrapped in `try`/`catch`. An archival failure is logged but **never escapes** to
  fail or delay the run.

**Upload ordering / idempotency.** The tree is uploaded first; **`manifest.json` is written LAST** as the
completeness marker — a consumer that sees `manifest.json` can trust the rest of the tree arrived. (The archiver
also excludes any stale `manifest.json` from the tree walk so it never uploads an old copy ahead of the freshly
built one.) Uploads use `overwrite`, so re-archiving the same `swarmId` is idempotent and self-heals a partial
prior run. There is no bespoke retry loop — transient faults rely on the Azure SDK's built-in retry.

> **Durability note.** The pipeline is in-process: the bounded channel applies back-pressure
> (`FullMode.Wait`) rather than dropping completion notifications, but a pod kill *mid-copy* can still drop that
> one archive. On graceful shutdown the background service best-effort drains whatever is already queued, giving an
> in-flight copy a short window to finish. If fully-durable, cross-pod archival is later required, the producer
> depends only on `ISwarmNotificationPublisher`, so a durable transport can replace the in-process channel as an
> additive change — the dispatcher that publishes the notification does not change.

---

## 6. The `manifest.json` and `task-outputs.json` artifacts

### `manifest.json` — run metadata (one per archived run)

Lets a consumer index/query runs without parsing transcripts:

```jsonc
{
  "swarmId": "...", "goal": "...", "templateKey": "...",
  "finalState": "Complete | Cancelled | Failed", "failureReason": null,
  "createdUtc": "...", "completedUtc": "...",
  "agents": [ { "name": "security-reviewer-1", "role": "security-reviewer", "displayName": "..." } ],
  "context": { "sourceRoot": "..." }
}
```

The roster is read verbatim from the archived `.chat/agents.json` (`[{ name, role, displayName }]`); it is an empty
array when no roster was written. `context` carries the per-swarm run-context bag supplied at creation — a flat
string-to-string map sourced from the `ISwarmRunContext` values (see
[template-custom-tools.md](template-custom-tools.md)); it is an empty object `{}` when no context
was supplied.

### `task-outputs.json` — agent-attributed output index

Written by the orchestrator on **every** run (including Failed), so it travels inside the archived tree. It pairs
each completed task's output with the **producing agent**, giving downstream a deterministic
`(input, output, label-join-key)` triple per lens — the corpus for user-led, per-lens training:

```jsonc
{
  "swarmId": "...", "templateKey": "...", "completedUtc": "...",
  "tasks": [{
    "taskId": "...",
    "workerName": "security-reviewer-1",   // instance
    "workerRole": "security-reviewer",      // LENS — the join key training buckets by
    "subject": "...",                        // the task subject (input half)
    "result": "...",                         // VERBATIM task result (round-trips structured JSON unchanged)
    "completedUtc": "...",                   // v1: the task's last-update timestamp
    "artifacts": {
      "transcript": ".chat/security-reviewer-1.jsonl",
      "systemPrompt": ".chat/security-reviewer-1.system.md",
      "systemPromptHash": "sha256:…"         // pins the prompt version for drift detection (null when absent)
    }
  }]
}
```

The shape maps directly onto `TaskOutputsIndex` / `TaskOutputEntry` / `TaskArtifacts`
(`../src/Swarmwright/Models/`). A consumer joins a human verdict → `workerRole`/`taskId` → that agent's
`transcript` reasoning. The framework emits the stable join keys and verbatim outputs only; it has no knowledge of
downstream "findings" models.

---

## 7. Security

Promoting the corpus from ephemeral host-local files to durable storage raises the data-handling bar:

- **Private container only** — no anonymous/public access; access is via the configured MI / service-principal
  credential.
- **No artifact-content logging** — logs carry `swarmId`, file/byte counts, and outcome only; never
  transcript / prompt / result bodies (they may contain customer or repo data).
- **No credential-value logging** — secrets, tokens, and the resolved credential are never logged.

---

## 8. Retention

There is no TTL/retention engine in the framework. Use **Azure Blob lifecycle management** on the container for
expiry and tiering.
