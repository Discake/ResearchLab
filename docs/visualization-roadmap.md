# ResearchLab — Visualization Roadmap

> Status: proposal / future direction · 2026-10-24
> Not an approved expansion of the current MVP. See [VISION.md](../VISION.md), [DOMAIN.md](../DOMAIN.md), [IMPLEMENTATION_PLAN.md](../IMPLEMENTATION_PLAN.md).

## Goal

ResearchLab should eventually support not only scalar function plots and benchmark metrics but also **scientific and engineering visualizations**: 2D/3D meshes, geometries, scalar/vector fields, time-dependent data and custom research dashboards.

A Research Story should be able to embed such views as evidence-backed interactive blocks. **Recorded data and its visualization are different concepts.** Graphs and viewers must not fabricate experimental results.

## Three levels of capability

### Level 1 — Standard chart builder (M5 / M6, modest scope)
- Select a Run or multiple Runs, metric series, axes, chart style, labels, units, scale and legend.
- Initially support line and scatter plots with basic comparisons; other built-in plots can follow.
- Save a declarative chart definition tied to **specific** runs and recorded metrics.
- Allow insertion of charts into Research Story, including a portable read-only export.
- No arbitrary code, no drag-and-drop page editor.

### Level 2 — Composable research panels (after MVP)
- Compose a small set of built-in blocks (plots, tables, Markdown, images, geometry viewers) in a grid or split layout.
- Save layout and presentation state, including selected data, camera position and optional time step.
- Support 2D triangle/quad meshes, 3D models/meshes, fields and overlays *incrementally*, based on concrete project needs.
- Support side-by-side comparison of geometry or runs where suitable.
- Do **not** commit to a full notebook or visual app builder.

### Level 3 — Custom embedded visualizations (future, separate design approval)
- Permit an author to supply a custom visualization, e.g. an interactive RBF mesh viewer or unique simulation UI.
- Prefer a web-based isolated viewer with a versioned, limited read-only data/API contract.
- Treat custom JS/HTML and third-party bundles as **untrusted executable content**.
- A sandboxed iframe alone is insufficient as a security design: define dedicated origins, content security policy, message validation, access scopes, asset limits, and publication constraints before implementation.
- Public publications must not gain access to local APIs, host files or unrestricted code execution.

## Data and renderer separation

For current M4, keep the existing **MetricPoint** and immutable **Artifact** contracts. Allow supported output files (CSV, images, VTK variants, GLB/glTF, etc.) as artifacts without promising native rendering of every format.

When implementing visualization features, introduce a versioned **VisualizationDefinition / VisualizationSpec** in the application/presentation boundary, linking immutable run IDs, metric series and/or artifact IDs to a declarative view configuration. Do not introduce an unneeded generic plugin interface or a renderer-specific concept into Domain in M3/M4.

Potential future renderers to evaluate:
- Plotly.js for common analytical graphs and some 3D plotting.
- vtk.js for specialized scientific meshes/fields.
- Three.js for general-purpose interactive models and geometry.

These are candidates, **not dependency decisions**. Validate support for actual file formats, data sizes and portable offline/static publishing before adoption.

## Milestone boundaries

| Phase | Visualization commitment |
| --- | --- |
| M3 | None: trusted process execution, logs and persisted Run history only. |
| M4 | Record metrics and files safely; do not implement mesh rendering. |
| M5 | Minimal configurable line/scatter comparisons of real metrics. |
| M6 | Embed evidence-linked plots in Markdown Research Stories and static exports. |
| Post-MVP V1 | Built-in 2D/3D viewers for selected artifact formats. |
| Post-MVP V2 | Composite panels, overlays, fields, animation and run comparisons. |
| Later | Custom author-provided interactive components after security design. |

## Non-negotiable constraints

1. A visualization points to **specific immutable evidence**; never silently resolve to “latest Run.”
2. Precomputed demo results must be labeled; user interactions must not imply live computation when there is none.
3. A static publication must remain viewable without the original backend; choose fallback images or bundled data when an interactive renderer is unavailable.
4. Respect large-file, memory and browser performance limits; don't load arbitrarily huge meshes by default.
5. Separate author/editor privileges from public read-only access.
6. Preserve source files and checksums; derived previews must be explicitly distinguished.
7. Do not expand M3 or M4 to implement speculative viewer/plugin architecture.

## Open questions (post-MVP)

- What is the first concrete research data type to support: structured 2D mesh, VTK unstructured mesh, or glTF model?
- Which viewer functionality is essential: rotate/pan/zoom, wireframe, scalar coloring, probe, slicing, playback?
- How much custom JS can appear in a shareable static publication?
- Should programs supply recommended visualization metadata, while authors override the final display settings? **Tentative preference: yes**, with validated recommendations and author-controlled presentation.
- What portable fallback should be exported for oversized/unsupported 3D scenes?

**Decision record:** These are directions to explore, not required deliverables until a later milestone is explicitly approved.
