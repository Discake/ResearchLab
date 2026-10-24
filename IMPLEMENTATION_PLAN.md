# ResearchLab — Implementation Plan

> Status: proposed v0.1 · 2026-10-24
> Product direction: Lab-first, local-first.
> Governing documents: [VISION.md](VISION.md), [DOMAIN.md](DOMAIN.md), [AGENTS.md](AGENTS.md).
> This plan is executable in small, independently reviewable increments. It is not permission to implement every stage at once.

## 0. Delivery principles

- Build one end-to-end path: **import → experiment → run → evidence → Research Story → static publication**.
- Keep MVP a single-user modular monolith. Avoid CQRS frameworks, event buses, microservices, plugins and remote agents.
- Prefer a modest, explicit implementation over speculative reusable abstractions.
- Make every stage independently buildable and testable. Do not advance on failing tests.
- Work in focused branches/PRs if the user authorizes repository changes; never auto-merge.
- Update both English and Russian specifications when domain/product decisions change.
- Record architectural decisions with short ADRs **only when a consequential trade-off is actually made**.

## 1. Proposed stack and structure

- .NET 10 SDK, C#; ASP.NET Core for API and Blazor Web App for author UI.
- SQLite + EF Core for structured metadata.
- Filesystem for logs, artifacts and static publication exports.
- xUnit v3 for tests if appropriate in the actual installed toolchain (do not assume version compatibility before checking).
- C++20 + CMake for a small research benchmark sample.
- Git CLI for reading local revision/dirty state, not a full Git client.

Suggested initial solution (create only what each stage needs):

```text
ResearchLab.slnx
src/
  ResearchLab.Domain/
  ResearchLab.Application/
  ResearchLab.Infrastructure/
  ResearchLab.Execution/
  ResearchLab.Web/               # ASP.NET Core host + Blazor
tests/
  ResearchLab.Domain.Tests/
  ResearchLab.IntegrationTests/
samples/
  rbf-benchmark/                  # may begin as a simpler deterministic benchmark
docs/
  decisions/                      # add ADRs as needed
```

Dependency direction:

- Domain: no infrastructure/framework dependencies.
- Application → Domain.
- Infrastructure → Application + Domain.
- Execution → Application + Domain (interfaces owned by Application).
- Web → Application, Infrastructure and Execution only in composition root.
- Tests depend on tested modules.

Avoid adding a separate HTTP API project until remote clients actually need one; the Blazor host can expose thin HTTP endpoints if needed. Avoid a general plugin contract for MVP.

## 2. Milestones

### M0 — Repository bootstrap and architecture skeleton

**Outcome:** reproducible local development and a green empty test baseline.

Tasks:
- Inspect repository, docs, branch, SDK/toolchains and existing conventions.
- Create solution, minimal projects, project references, nullable enabled and warnings policy.
- Add `.editorconfig`, `.gitignore`, README with prerequisites and local commands.
- Add domain test project and one meaningful smoke test.
- Add a GitHub Actions workflow for restore/build/test *only if useful*, not an extended CI platform.

Acceptance:
- Fresh checkout builds and tests with documented commands.
- No business logic or database until stage M1.
- The project structure and dependency direction are documented.

### M1 — Domain core (no database, no UI)

**Outcome:** core domain invariants enforced by tests.

Implement:
- Typed IDs for Project, Experiment, Run (choose simple `Guid` wrappers or a consistent equivalent).
- Project + Experiment with `TaskKey` and typed parameter definitions.
- Parameter validation (required/default, numeric range, allowed values, unsupported type).
- Run with immutable parameters, experiment/task snapshot, source/environment snapshot.
- RunStatus state machine: Created → Running → Succeeded/Failed/Canceled/TimedOut/Interrupted; Created → Canceled.
- MetricPoint: finite numeric value, name, unit, optional step, ingestion order.
- Domain errors with clear reasons; no repository/network dependencies.

Tests:
- Valid and invalid parameter sets, default handling and type validation.
- Every allowed/disallowed status transition, repeated completion and terminal immutability.
- Run snapshots cannot change after creation/start.
- NaN/infinity rejected; ordered metrics with repeated step allowed.

Acceptance:
- Domain test suite is green.
- No SQL, process launch or UI code inside Domain.

### M2 — Persistence and project import

**Outcome:** a local project and experiment survive application restart.

Implement:
- SQLite EF Core context and initial migrations for Project/Experiment/Run/MetricPoint metadata.
- Local project import by path; canonicalization and validation.
- `researchlab.json` schema v1 parser (explicit executable, argument list, timeout); no shell expansion.
- Trust confirmation for the selected local task before it is executed. Importing never executes.
- Git HEAD, remote URL and dirty working-tree inspection via safe Git CLI argument list; non-Git paths remain valid.

Tests:
- SQLite persistence/reload using real ephemeral database.
- Invalid config/schema, nonexistent executable, non-Git workspace, dirty-tree detection.
- No automatic execution on import.

Acceptance:
- Project import, create Experiment, list/reopen after restart.
- Git data is best-effort, never presented as full reproducibility.

### M3 — Local execution, Run history and minimal CLI

**Outcome:** a trusted console executable can be launched, observed and canceled through a minimal CLI; run history remains available after restarting the tool.

Implement in small substeps:
1. **M3.1 — Application orchestration:** `StartRun(experimentId, parameters)`, `CancelRun(runId)`, `GetRun(runId)`, and `ListRuns(experimentId)`. Validate parameters, re-check trust immediately before launch, and persist frozen launch snapshots.
2. **M3.2 — ProcessRunner:** use `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, an explicit working directory and dedicated output directory. Drain stdout/stderr concurrently; enforce log limits, timeout and cancellation, and terminate the process tree where supported.
3. **M3.3 — Persistence and recovery:** save lifecycle transitions with optimistic concurrency; reconcile stale `Running` runs to `Interrupted` after a crash or restart; preserve diagnostics and prevent conflicting terminal updates.
4. **M3.4 — Minimal CLI:** add a thin, disposable-friendly console host calling Application use cases, not duplicating domain/execution logic. Provide at least project/experiment listing, a run start command, run history/detail, and a way to cancel a long-running run while the command is active. The CLI is an author-facing verification tool, not a second product.
5. **M3.5 — Integration tests:** verify the end-to-end CLI/use-case path, process success/failure, launch errors, cancellation, timeout, high-volume stdout/stderr and lifecycle races.

**Important CLI lifecycle decision:** in MVP M3, `run start` may remain attached to the executing process until it exits. It must not return immediately while abandoning an unowned background process. An always-on daemon, detached execution and cross-session cancellation are **out of scope**. If a separate terminal issues `run cancel`, document clearly whether it can signal an active host; do not claim cross-process cancellation without an actual coordination mechanism. At minimum, support cancellation in the active CLI session (for example, Ctrl+C), with `CancelRun` preserved as an application operation for later hosts.

Illustrative commands (final syntax can differ):

```sh
researchlab project list
researchlab experiment list
researchlab run start <experiment-id>
researchlab run list
researchlab run show <run-id>
```

Tests:
- Success/nonzero code, startup failure (`Created → Failed`), cancellation/timeout and large stdout+stderr output without deadlock.
- Concurrent cancellation/exit race produces a single terminal status.
- Restart reconciliation does not overwrite terminal runs.
- CLI can launch a real trusted sample program, present its logs and outcome, and show persisted runs after restart.
- Cancellation of an attached run is observable and persists `Canceled`.

Acceptance:
- Three launches with different valid parameter sets create distinct persistent Run records.
- Author can use the CLI without changing ResearchLab source code or manipulating the database.
- A long-running task can be canceled from its active CLI session.
- Failed, canceled and timed-out runs retain diagnostics and correct status.
- Historical runs remain visible after the CLI is restarted.
- No metrics protocol, charts, web UI, daemon, distributed worker or Story editor is required in M3.

### M4 — Protocol, metrics and artifacts

**Outcome:** experimental evidence is recorded with source provenance.

Implement:
- Versioned line protocol `@researchlab/v1 {json}` from stdout.
- Metric event parser and validation, bounded event lengths/counts, durable storage.
- Pass parameters via `RESEARCHLAB_PARAMETERS_JSON`; output directory via `RESEARCHLAB_OUTPUT_DIR`.
- Artifact registration restricted to run output directory, with symlink/canonicalization guards, quotas and SHA-256.
- File viewing/download through safe application endpoints (not raw arbitrary filesystem paths).
- C++ sample using the protocol and producing metric series + one artifact.

Tests:
- Mixed normal logs and structured events.
- Malformed/unknown events, unsupported units, nonfinite values, oversized input.
- Path traversal, absolute paths, symlink escaping output dir, oversized artifact.
- Artifact content checksum and immutability.

Acceptance:
- Three C++ sample runs persist distinct metric series and immutable artifacts.
- Bad events produce diagnostics without corrupting valid data.

### M5 — Usable Blazor workspace and comparisons

**Outcome:** the complete author workflow is operable through the browser.

Implement:
- Import project, create Experiment, choose parameters and start/cancel Run.
- Run list/detail with live or near-live log streaming.
- Git SHA and dirty-tree flag prominently visible.
- Compare recorded metrics across selected runs (one line/scatter plot is sufficient).
- Inspect/download artifacts.

Tests:
- Component/service tests for basic forms, invalid inputs and status displays.
- End-to-end happy path if toolchain supports it; otherwise document manual verification steps.

Acceptance:
- Author can operate the entire M0–M4 workflow without editing source code or DB.

### M6 — Research Story and static publication

**Outcome:** an employer can examine real evidence without a local ResearchLab installation.

Implement:
- ResearchStory draft: title, summary and Markdown body.
- Add references to exact run IDs, metrics/chart specs and chosen artifact IDs from the same Project.
- Safe Markdown renderer (sanitize HTML, external URLs as appropriate).
- Export an immutable standalone static HTML directory: story, plots/data, selected artifacts and Git references.
- Avoid leaking workspace paths, credentials, unselected files or APIs.
- Label precomputed visual/demo data honestly; no anonymous execution.

Tests:
- Export a story referencing fixed runs; future runs do not alter prior publication.
- Open export without backend/database connection.
- HTML/script injection and private-path/secret exposure checks.
- All exported links/assets resolve within the generated publication.

Acceptance:
- Exported Story opens in a browser on another machine without ResearchLab, C++ compiler, or API.
- Evidence links point to retained measurements, not mock values.

### M7 — Release hardening and showcase demonstration

**Outcome:** demonstrable MVP, not a promising scaffold.

Tasks:
- Verify all seven VISION.md MVP acceptance criteria.
- Publish a well-documented **example** Research Story from the C++ benchmark.
- Test clean installation, process cancellation, database migrations and static export.
- Document limitations and basic threat model; polish README with screenshots **from the running app**.
- Package a first local launch procedure; optional single-command dev startup.

Acceptance:
- Full reproducible walkthrough from fresh checkout.
- No unverified test claims or synthetic research results presented as real.

## 3. Main risk register

| Risk | Response |
| --- | --- |
| Scope creep | One acceptance test per increment; defer plugins, distributed workers and rich editors. |
| Process runner deadlock / leaked process | Drain both streams concurrently; test timeout/cancel and process-tree shutdown. |
| Unsafe arbitrary command execution | Explicit trust; argument-list execution; only local owner can authorize jobs. |
| Artifact path traversal | Dedicated output directory, resolved paths, symlink checks, quotas. |
| Misleading reproducibility | Capture Git + dirty flag + environment metadata; show limitations. |
| Export leaks sensitive paths/data | Explicit artifact selection; sanitize Markdown; static export tests. |
| SQLite write contention under metric ingestion | Batch writes and control concurrency; optimize only with measurements. |
| Documentation drift | Update EN/RU versions together on contract changes. |

## 4. Suggested PR-sized agent tasks

1. `chore/bootstrap`: solution, references, formatting, README, tests.
2. `feat/domain-core`: Project, Experiment, parameters, Run status and tests.
3. `feat/sqlite-catalog`: local project import, Git inspection, persistence.
4. `feat/local-runner`: subprocess lifecycle, logs, failures and cancellation.
5. `feat/metric-protocol`: event parser, metric persistence, output artifact registration.
6. `feat/cpp-sample`: a real deterministic C++ benchmark with recorded output.
7. `feat/web-workspace`: Blazor basic author flow and charts.
8. `feat/research-story`: Markdown stories and immutable export.
9. `chore/mvp-validation`: end-to-end demo, docs, security review.

Prefer one reviewable task at a time. The stages describe order, not a requirement to create all projects/interfaces immediately.

## 5. First agent assignment (copyable prompt)

> Read AGENTS.md, VISION.md, DOMAIN.md and their Russian versions in Discake/ResearchLab. Implement **M0 only** on a new branch `chore/bootstrap`. Inspect repository state and installed toolchain first. Bootstrap the minimal .NET solution with Domain, Application, Infrastructure, Execution, and Web host only if that structure is justified by an immediate need; a smaller initial structure is acceptable. Add a test project, a useful first smoke test, .editorconfig, .gitignore and README with exact commands. Do not implement database, task engine, experimental features or rich UI yet. Run restore/build/test and report actual results; if dependencies are unavailable, explain blockers rather than claiming success. Explain project references and any differences from the plan. Open a PR for review if authorized; do not merge it or modify main directly.

## 6. Reporting template for every agent iteration

- **Goal and scope:** what behavior was implemented.
- **Changes:** files/modules touched; important decisions.
- **Verification:** exact build/test commands and their outcomes.
- **Known gaps/risks:** what has not been implemented or verified.
- **Next increment:** one suggested next task, not a sweeping rewrite.
