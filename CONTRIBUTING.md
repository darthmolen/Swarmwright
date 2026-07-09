# Contributing to AgentMemoryOS

Thanks for your interest in contributing! This is a small, focused library; contributions of
all sizes are welcome — bug reports, docs, and code.

## Ground rules

- Be respectful — see the [Code of Conduct](CODE_OF_CONDUCT.md).
- Open an issue before large changes so we can agree on direction.
- Keep PRs focused; one logical change per PR.

## Development setup

Everything you need to build, run the example, and exercise the test suites is in
[HOW-TO-DEV.md](HOW-TO-DEV.md). In short:

```bash
dotnet build AgentMemoryOS.slnx          # warnings-as-errors, full StyleCop + analyzers
dotnet test  tests/AgentMemoryOS.Tests   # unit tests, no external dependencies
dotnet test  AgentMemoryOS.slnx          # everything (Testcontainers spins up pg + redis)
```

## Coding standards

The libraries build under a strict gate: Centralized Package Management, `TreatWarningsAsErrors`,
`AnalysisMode=all`, full StyleCop, and required XML docs on public members. The full conventions
(naming, file format, packaging) live in [CLAUDE.md](CLAUDE.md). Your build must be **warning-free**.

- Add or update tests for any behavior change (unit tests for logic; Testcontainers integration
  tests for the Postgres/Redis stores).
- Public API additions need XML documentation.
- Match the surrounding style; `.editorconfig` + StyleCop are the source of truth.

## Pull request process

1. Fork (or branch, if you have write access) and create a topic branch.
2. Make your change with tests; ensure `dotnet build` and `dotnet test tests/AgentMemoryOS.Tests` pass.
3. Open a PR against `main`. CI (build + unit tests) must be green.
4. A maintainer reviews and merges. `main` requires a PR; the solo maintainer may bypass when needed.

## Commit messages

Use clear, imperative summaries (e.g. "Add Redis TTL option"). Group related changes; avoid
"misc fixes" commits.

## Versioning

This project follows [Semantic Versioning](https://semver.org). Releases are cut by tagging
`vMAJOR.MINOR.PATCH`, which triggers the publish workflow.
