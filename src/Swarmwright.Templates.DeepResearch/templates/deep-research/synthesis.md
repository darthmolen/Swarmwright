# Deep Research Report

You are synthesizing the results from a deep research team that investigated the following topic:

**Goal:** {goal}

## Task Results

{task_results}

## Synthesis Instructions

Produce a comprehensive research report with the following sections:

### Executive Summary

Provide a concise overview of the research question, the key findings, and the overall confidence level of the conclusions. State the most important takeaway in the first sentence. Keep this to 4-6 sentences.

### Key Findings

Present the primary researcher's most important discoveries, organized by relevance to the research question. For each finding:
- State the finding clearly
- Note the evidence quality (Strong / Moderate / Weak)
- Include source attribution

### Evidence Analysis

Evaluate the overall evidence base:
- Where do multiple sources converge on the same conclusion?
- Where is the evidence contradictory or insufficient?
- What are the most reliable data points versus the most uncertain?

### Contrarian Perspectives

Summarize the skeptic's most important challenges:
- Which assumptions were identified and how do they affect conclusions?
- What biases were detected in the source materials or framing?
- Which alternative explanations are most credible?
- How do these challenges modify the primary findings?

### Data Insights

Present the data analyst's quantitative findings:
- Key metrics with context and benchmarks
- Significant trends and their implications
- Statistical validation or challenges to qualitative claims
- Data quality limitations

### Confidence Levels

For each major conclusion, assign a confidence level:
- **High confidence** — Supported by multiple independent sources, consistent with quantitative data, survives skeptical scrutiny
- **Moderate confidence** — Supported by credible evidence but with notable gaps, alternative explanations, or limited data
- **Low confidence** — Based on limited evidence, contradicted by some data, or significantly challenged by skeptical analysis

### Recommendations

Provide actionable next steps:
- Decisions that can be made now based on high-confidence findings
- Areas requiring additional research before decisions should be made
- Specific follow-up questions that would most improve confidence levels
- Risks of acting on low-confidence conclusions

### Sources

Compile a deduplicated list of all sources cited by the research team, organized by type:

**Web Sources** — URLs fetched during research, presented as markdown links `[Title](url)`

**Data Sources (MCP)** — Tool calls that produced quantitative claims, presented as `[MCP: provider/tool] query/params`

**Unsourced Claims** — Any finding tagged `[model-knowledge]` by the workers. These are not independently verified from this session's research and should be treated as lower confidence.

## Source Integrity Rules

- Every Key Finding MUST have at least one sourced citation (web URL or MCP reference). If a finding has no source from the workers' results, demote it to the Unsourced Claims list — do NOT present it as a verified finding.
- Preserve the workers' original URLs and MCP references verbatim — do not paraphrase source attributions into vague phrases like "according to research."
- If multiple workers cite the same source, list it once and note which workers referenced it.
