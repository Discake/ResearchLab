# ResearchLab — Applied Engineering Research Roadmap

> Status: proposed post-MVP capabilities · 2026-10-24
> Related: [Vision](../VISION.md), [Domain](../DOMAIN.md), [Research Story](research-story-concept.md), [Implementation plan](../IMPLEMENTATION_PLAN.md).
> These capabilities are **not** new requirements for M3–M6 unless separately approved.

## Audience and positioning

ResearchLab is for engineers who need to **compare technical approaches, inspect reproducible evidence, and communicate decisions**. This includes independent developers, numerical/R&D engineers, backend/performance teams, embedded/telecom developers, and architects validating proof-of-concept implementations.

Not intended to replace production monitoring, CI/CD, product A/B testing, issue trackers, or generic BI.

## A — Baseline runs and regression comparison

- An author can explicitly **pin one Run as the reference baseline** for a specific Experiment and comparison context.
- Baseline is an exact immutable Run ID; it must **never** automatically track “the latest” run.
- Compare measured values/series from selected candidate runs with that pinned reference; show absolute and relative deltas, units, parameter/configuration differences, and missing metrics.
- Clearly distinguish **Run status** (execution succeeded) from **comparison result** (regression, improvement, inconclusive).
- Require/indicate comparability: environment, input dataset, conditions, parameters and metric semantics. Flag differences and never silently declare two runs comparable.
- Changing the selected baseline changes a *versioned assignment* or creates a new selection/history entry; it never changes prior Run records or already exported Publications.
- Later: baseline per scenario/environment and automatic regression detection with configurable, meaningful thresholds.

**Example:** assign Run A as baseline for API p95 latency; compare B and C. A passing execution can still have a performance regression.

## B — Experiment batches and parameter matrices

- Generate a bounded matrix of validated parameter combinations and schedule explicit repeated runs.
- Store grouping/batch metadata and preserve each Run's own parameters/provenance.
- Present progress, failures and comparisons between configurations.
- Add queueing/distributed execution only after the local-runner MVP; enforce execution/resource limits.

## C — Acceptance criteria / quality gates

- Allow users to declare criteria such as `p95_latency_ms < 100`, error fraction, or max memory.
- Evaluate against actual stored measurements with explicit units, aggregation semantics and comparability requirements.
- Results distinguish **Pass / Fail / Inconclusive / Not evaluated**.
- **Never conflate RunStatus.Succeeded with an acceptance result.** A successful process can fail a criterion; missing evidence does not equal pass.
- Make the criterion, data source, calculation, threshold and evaluation version inspectable.
- Do not claim mathematical or scientific proof from an automated quality gate.

## D — Experiment context and provenance

- Capture available environment metadata (OS, architecture, runtime/toolchain, hardware), input dataset identity/checksum, source Git SHA and dirty-tree status, execution configuration, tags and meaningful scenario labels.
- Display what is known, unknown and potentially incomparable.
- Do not export local secrets, paths or environment variables without explicit approval.
- Support richer environment fingerprints later, without asserting exact reproducibility where insufficient.

## E — Technical Decision Report

- Optional Research Story template: **Problem → Alternatives → Criteria → Methodology → Results → Risks/Costs → Decision → Limitations**.
- Reference exact baseline and candidate runs, metrics, artifacts and Git revisions.
- Explain trade-offs; allow unresolved outcomes. ResearchLab should not decide which architecture is “best” without declared criteria.
- Make reports shareable as immutable, read-only Publications.

## Evidence links and assertion inspection (cross-cutting feature)

**Core user experience:** a reader encounters an engineering claim and can immediately inspect the evidence used to support or challenge it.

Supported navigation patterns to explore:
1. **Deep link / anchor:** click a claim reference to jump directly to an evidence block, figure, table, run, comparison or exact section, with a stable shareable URL fragment.
2. **Inspect evidence button:** open a contextual inspector (panel/modal/popover) without losing reading position, presenting the claim's sources and letting the reader drill into complete data.
3. **Back-links:** from an evidence block, navigate back to statements referencing it.

The inspector should make practical checking fast:
- Show claim text, scope and assumptions;
- Link to exact Run IDs, parameters, Git SHA, dirty-tree warning, environment and artifacts;
- Show metric names, units, sampling/aggregation, baseline/candidate comparisons and missing data;
- Distinguish author interpretation from recorded observations;
- Expose an explicit, understandable calculation where the claim depends on a derived quantity;
- Surface contradictory or insufficient evidence rather than concealing it.

**Do not label evidence links as automatic “proof”.** Prefer “Supporting evidence”, “Inspect evidence” and “Verification details”. Mathematical proof, statistical confidence, benchmark repeatability and engineering acceptance are different epistemic standards. If a claim is marked “verified”, its scope, criterion, procedure, and actor should be recorded; the label must not imply scientific certification.

### Illustrative authoring representation (not an approved schema)

```md
The optimized implementation reduced p95 latency under the tested load.
{{evidence:comparison-42}}
```

Possible model later: `Claim` + `EvidenceReference` + `EvidenceSet`, anchored to a Story revision and immutable Runs/Artifacts. **Do not introduce these domain entities during M3/M4 or solely for this proposal.** In early M6, simple explicit evidence links and anchors may suffice; rich contextual inspection can be implemented incrementally.

### Integrity of shared publications

- Stable anchors and deep links must work in static read-only exports without the author's backend.
- Bundle selected referenced evidence and derived calculations needed by the inspector; broken/unpublished evidence must be flagged at export.
- Published snapshots and baseline assignments displayed in them are immutable.
- Never substitute a new Run, new baseline or recalculated evidence after publication.
- All charts/statistics are derived from genuine saved or explicitly attributed data; mock figures must be labeled.
- No public execution access is granted by inspecting evidence.

## Rollout proposal

| Stage | Scope |
| --- | --- |
| M3–M4 | No new required work; keep Run provenance and immutable recorded metrics/artifacts. |
| M5–M6 | Basic exact evidence references and clickable links/anchors in Story; optionally simple inspect view if low cost. |
| Post-MVP P1 | Pinned baseline Run, manual run comparisons and delta calculations, evidence inspector. |
| Post-MVP P2 | Acceptance criteria with explicit status, batches/parameter matrices, stronger comparability checks. |
| Post-MVP P3 | Rich Technical Decision Reports, derived statistics, team-oriented reporting workflows. |

## Open questions

- Baseline scope: per Experiment, scenario, hardware class, dataset or manually named comparison set?
- What makes two runs comparable by default; which differences require explicit acknowledgment?
- Should claims be formal entities or annotated spans/anchors in Markdown?
- How to include counterevidence or uncertainty in the inspector?
- Which evidence and calculated summaries are bundled in a compact static Publication?

**Principle:** fast practical verification must remain possible without turning the app into a formal theorem prover, a scientific certification authority or another CI/CD service.
