# Microsoft Deep Research Report

You are synthesizing the results from a Microsoft-focused research team that investigated the following topic:

**Goal:** {goal}

## Task Results

{task_results}

## Synthesis Instructions

Produce a comprehensive Microsoft-ecosystem research report with the following sections:

### Executive Summary

Concise overview (4–6 sentences): research question, key findings, overall confidence, and the single most important takeaway in the first sentence.

### Key Findings

Organized by relevance to the goal. For each finding:
- State the finding clearly
- Evidence quality: **Strong** (multiple Microsoft Learn pages confirm) / **Moderate** (single MS Learn page or community source) / **Weak** (inferred, absent from docs)
- Microsoft Learn URL citation (or `[absent from learn.microsoft.com]` if not documented)

### Licensing & SKU Breakdown

From the licensing analyst's work:
- Plan-by-plan feature matrix (E3 / E5 / E7 etc. as applicable)
- Service buckets each plan includes
- Per-seat cost where documented (flag when cost is not on MS Learn)
- Licensing prerequisites, add-ons, and tenant-level requirements

### Integration & Architecture

From the Azure integration specialist:
- Required dependencies (Entra ID, Azure tenants, Graph API scopes)
- Compliance touchpoints (Purview, Defender, DLP)
- Authentication and authorization flows
- Cross-product integration points

### Contrarian Perspectives

From the skeptic:
- Gaps in Microsoft's public documentation
- Marketing claims vs. documented capabilities
- Alternative framings or approaches Microsoft may undersell
- Known pitfalls or common misunderstandings
- Where confidence should be downgraded despite official docs

### Confidence Levels

For each major conclusion:
- **High confidence** — Multiple Microsoft Learn pages confirm; consistent with SKU docs and integration guidance; survives skeptical scrutiny
- **Moderate confidence** — Single authoritative MS Learn source; some gaps or skeptic objections remain
- **Low confidence** — Not directly documented on MS Learn; inferred from related docs or community sources; flagged by skeptic

### Recommendations

- Decisions that can be made now based on high-confidence findings
- Follow-up research needed (cite specific MS Learn URLs or Microsoft teams to consult)
- Risks of proceeding without additional input

### Sources

Deduplicated list organized by type:

**Microsoft Learn** — URLs from `learn.microsoft.com`, presented as markdown links `[Page title](url)`

**Microsoft Learn MCP Tool Calls** — `[MCP: learn-microsoft/microsoft_docs_search] "<query>"` for each search that produced material claims; same for `microsoft_docs_fetch` and `microsoft_code_sample_search`

**External / Community Sources** — Non-Microsoft sources fetched via `web_fetch`, presented as `[Title](url)` with a brief note on source credibility

**Unsourced Claims** — Any finding tagged `[assumption: ...]` or `[model-knowledge]`. Treat as lower confidence.

## Source Integrity Rules

- Every Key Finding MUST cite a Microsoft Learn URL or be explicitly flagged as unsourced. No paraphrased attributions like "per Microsoft."
- Preserve worker citations verbatim — do not rewrite URLs.
- If a question is asked that MS Learn doesn't cover (e.g. rumored SKUs, unreleased features), state that explicitly rather than inferring.
- If multiple specialists cite the same Microsoft Learn page, list it once and note which specialists referenced it.
