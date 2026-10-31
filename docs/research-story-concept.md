# ResearchLab — Research Story Concept

> Status: design direction, not a new implementation mandate · 2026-10-24
> Related: [VISION](../VISION.md), [DOMAIN](../DOMAIN.md), [Implementation plan](../IMPLEMENTATION_PLAN.md), [Visualization roadmap](visualization-roadmap.md).

## Purpose

**Research Story is an evidence-backed, interactive engineering publication**, combining a technical narrative, an experiment record, and an optional demonstration of the underlying project. It should help reviewers understand what was built, why, how it evolved, and how well the reported results are supported.

**Research Workspace** is the author's operational area for importing projects, launching experiments, examining logs, and exploring outputs. **Research Story** is the curated, reader-facing account. The published story must not expose author-only execution capabilities.

ResearchLab is not merely a Markdown editor with charts: its distinctive value is preserving an explicit relationship between claims and concrete research evidence.

## Three conceptual areas

1. **Narrative:** author-defined sections using Markdown, code, equations, images, tables, and explanations.
2. **Evidence:** references to specific Runs, metric series, charts, artifacts, source revisions, execution parameters, environments, test results, and acknowledged limitations.
3. **Publication:** a portable, read-only, immutable export of a story and explicitly selected evidence, understandable without the author’s local service.

These are conceptual responsibilities, **not a requirement for three services or three aggregate roots**.

## Building blocks (not an MVP class hierarchy)

| Block | Purpose | Priority |
| --- | --- | --- |
| Markdown / headings / text | Narrative structure, links, lists | MVP |
| Code | Highlighted source examples | MVP; can begin as Markdown fences |
| Equation | Mathematical notation | MVP if a safe portable renderer is feasible |
| Image | Figures and captions | MVP |
| Run | Provenance and outcome of an exact run | MVP |
| Chart | Recorded metric series / comparisons | MVP |
| Artifact | View or download a deliberately selected file | MVP |
| Comparison | Structured comparison of multiple runs | Future; MVP chart comparison is enough |
| Git history | Commit details and evolution | Future; MVP records SHA and dirty state |
| Mesh / 3D viewer | Engineering meshes and geometry | Post-MVP |
| Interactive demo | Switch precomputed examples; later controlled execution | Post-MVP |
| Custom visualization | Author-provided programmed viewer | Later, subject to security review |

For MVP, **do not introduce one domain class per block**. Markdown plus a small, versioned set of explicit evidence references or directives (illustrative syntax: `{{run:run-id}}`, `{{chart:chart-id}}`) is sufficient. The notation is a concept, **not an approved parser contract**. Resolve and validate references at authoring/export time.

## Flexible story structures

Do not force one universal template. Offer optional authoring templates later:

- **Numerical algorithm:** problem → method → experiments → accuracy/performance → engineering decisions → limitations.
- **Backend or infrastructure:** requirements → architecture → implementation → testing → observability → trade-offs.
- **Simulation:** model → hypothesis → methodology → results → interpretation → limitations.

Sections remain editable and optional. A reader should quickly grasp the research question, central results, and constraints.

## Evidence links — central product idea

An author can connect a statement, paragraph, chart or section to exact supporting objects:
- Run ID and its immutable launch snapshot;
- measured metric points or a chart derived from them;
- chosen artifacts and checksums;
- Git SHA, repository dirty status and recorded environment;
- tests/benchmarks when captured as evidence.

The interface should allow readers to inspect supporting parameters, versions and raw observations. **A link to evidence is not proof that a claim is scientifically valid.** ResearchLab does not certify claims, automate peer review, or manufacture benchmark results.

Story references must use **stable identifiers**, never implicit “latest run” resolution. A published revision includes or explicitly preserves access to the precise evidence on which it depends.

## Authoring and publication UX

Suggested MVP flow:
1. Create a Story within a Project.
2. Enter a title, summary and Markdown body.
3. Select specific Runs, metrics/charts and Artifacts from that Project.
4. Preview rendered text and evidence components.
5. Export self-contained static HTML and selected assets/data.
6. Open the published page on a different computer without a running ResearchLab backend.

The first editor can be a plain Markdown textarea with preview and simple evidence insertion. **Do not build a Notion-like editor, drag-and-drop layout engine or general-purpose plugin host in MVP.**

The public page is read-only. Interactive plots may explore **bundled measurements**, and precomputed demonstrations must be labeled accordingly. Arbitrary online execution by viewers is not part of M6.

## Integrity, safety and portability

- Do not present fixture data, illustrative plots, precomputed simulations or generated imagery as measured experiment output.
- Charts derive from saved metrics; external/manual material must be clearly attributed or labeled.
- Pin references to concrete Run/Artifact IDs, not mutable aliases.
- Treat a Publication as an immutable snapshot. Editing a Story creates a different export, never modifies prior published evidence.
- Sanitize Markdown and untrusted content and restrict URL schemes; custom scripts are not allowed in MVP.
- Export only explicitly selected artifacts. Omit absolute local paths, secrets, private configuration and execution authority.
- Make charts viewable offline using bundled data/assets or appropriate fallbacks. Avoid dependencies on the local database/API.
- A dirty Git tree or missing environment details must remain visible as reproducibility limitations.
- Consider accessibility, readable captions, units and methodology rather than treating charts as decoration.

## Scope alignment with the roadmap

- **M3:** no Story work; process execution and Run history only.
- **M4:** persist genuine metrics and immutable artifacts.
- **M5:** show evidence and configure basic line/scatter comparisons.
- **M6:** Markdown Story drafts, simple evidence references, preview, immutable static export.
- **After MVP:** richer templates and comparison/history blocks; 2D/3D and composable dashboards; controlled demos; custom viewers only after a security design.

## Open design decisions

- Inline evidence directive syntax versus a structured document AST.
- Whether formal Claim/Evidence entities are needed later; **not needed for MVP**.
- How to serialize chart definitions and bundle their data in static HTML.
- Best way to render portable equations and previews.
- How to preserve rich interactive content offline without exposing untrusted scripts.

**Guiding test:** a hiring reviewer should be able to read an exported Story, understand the engineering decisions, inspect which exact runs substantiate the conclusions, and view the results without compiling the project.
