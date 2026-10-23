# ResearchLab — Product Vision

> Status: Draft v0.1 · 2026-10-24
> Working name: ResearchLab
> Selected direction: **Lab-first** (local experiments → publishable Research Story)

## 1. One-sentence vision

**ResearchLab turns an engineering repository into a reproducible, explorable research project:** run experiments, collect evidence, compare revisions, and publish an interactive Research Story that others can inspect without installing the development toolchain.

ResearchLab is **not another CI/CD dashboard** and not a replacement for an IDE.

## 2. The problem

A repository alone rarely answers the questions a reviewer cares about:

- What problem was solved, and why was this approach chosen?
- Which experiments were run, with what parameters and code revision?
- How did correctness, accuracy, or performance change over time?
- Can the results be inspected and reproduced?
- Can the project be explored without setting up a complex environment?

Today these answers are scattered among READMEs, scripts, CSVs, screenshots, and commit history.

## 3. Target users

**Primary — author/researcher:** an engineer building algorithms, numerical models, performance benchmarks, infrastructure, or other experiment-driven pet projects. They need a small local tool that makes experiments repeatable and presentable.

**Secondary — reviewer/employer:** a person evaluating an engineering project. They need a short story, transparent evidence, Git references, charts, and understandable demonstrations accessible by a link.

## 4. Value proposition

A ResearchLab project connects four things:

1. **Source:** a Git revision and an explicit project configuration.
2. **Execution:** a run with captured parameters, environment information, state, logs, and exit result.
3. **Evidence:** metrics, charts, output files, and comparisons across runs.
4. **Narrative:** a Research Story explaining hypotheses, decisions, experiments, limitations, and conclusions.

A story links to **specific immutable run results**, not vaguely to the latest build.

## 5. Two distinct experiences

### Author: local research workspace

1. Register an existing local project/repository.
2. Declare one or more *trusted* build/run commands in `researchlab.json`.
3. Define an experiment and its parameter schema.
4. Launch a run, read streaming logs, and capture structured metrics and artifacts.
5. Compare several runs and pin meaningful charts to a Research Story.
6. Export a standalone, read-only publication.

### Reviewer: public research page

1. Open a URL with no software installation or account.
2. Read the problem, method, results, limitations, and history.
3. Inspect interactive plots and the exact runs/commits supporting claims.
4. Explore **precomputed** example scenarios in MVP.

**Important distinction:** the MVP does **not** let anonymous visitors execute arbitrary code. A future controlled remote demo may offer a fixed set of allowed parameters and isolated jobs with time/resource quotas. Precomputed demonstrations must be labeled as such.

## 6. MVP: one end-to-end vertical slice

The first release must support **one real C++ or C# console research project**:

- Import a local directory with optional Git repository.
- Read a minimal `researchlab.json` with trusted command definitions.
- Run one configured executable with user-selected, validated parameters.
- Capture lifecycle status, stdout/stderr, exit code, duration, Git HEAD, and working-tree dirty flag.
- Accept structured JSON Lines events for numeric metrics and registered artifacts.
- Persist metadata in SQLite and files in a local artifact directory.
- Show run history and compare metric series on a graph.
- Write a Research Story in Markdown, attaching fixed runs/charts/artifacts.
- Export a read-only HTML publication with bundled selected data; publish it using ordinary static hosting.

The UI may start with Blazor; no desktop shell or account system is required.

### Concrete demonstration

A benchmark investigates how RBF interpolation time and error change with the number of control points. Its runs vary a parameter, emit `duration_ms` and `residual`, and optionally produce a mesh file or image. Research Story shows the method, graph, Git revisions, observations, and limitations.

## 7. MVP acceptance criteria

The MVP is complete when someone can:

1. Import the sample benchmark and execute at least three different configurations.
2. Inspect live or near-live logs and the final result of every run, including an unsuccessful run.
3. View parameters and Git revision attached to any run; clearly see when working-tree changes make exact Git-only reproduction impossible.
4. Compare at least one metric across runs on a chart.
5. Open artifacts retained from a run.
6. Create a Markdown Research Story linked to specific runs and export it to a static HTML folder.
7. Open that publication on another machine in a browser, without ResearchLab or the compiler installed.

An author should be able to complete the main flow without editing ResearchLab source code.

## 8. Product principles

- **Evidence before decoration:** no invented metrics or claims; charts originate from saved run data.
- **Reproducibility is explicit:** save the configuration and Git reference; distinguish a clean repository from a dirty working tree. Do not label a run fully reproducible merely because it has a SHA.
- **Language-agnostic core:** any executable that follows the minimal event protocol can participate.
- **Progressive complexity:** local execution first; remote agents later.
- **Safe defaults:** only explicit trusted commands run locally; never execute code from public visitors in the author’s workspace.
- **Portable storytelling:** public reports must remain readable without the original database or agent.
- **Transparent limitations:** record failures, environment dependencies, and benchmark conditions.

## 9. Explicit non-goals for MVP

- General-purpose CI/CD and hosted Git runners.
- Multi-user permissions, collaboration, or marketplace of plugins.
- Kubernetes, distributed scheduling, Redis, or message brokers.
- A full Notion-like rich text editor.
- Arbitrary interactive execution by unauthenticated visitors.
- Automated scientific validity checks or guaranteed reproduction of uncommitted source changes.
- An IDE, source-code editor, or full Git client.

## 10. Evolution beyond MVP

1. **Improved research UX:** experiment presets, reproducible environment descriptors, richer chart types, reusable Story sections.
2. **Language SDKs:** ergonomic C# and C++ clients over a stable event protocol.
3. **Controlled online demos:** isolated execution images, bounded parameters, queues, quotas, and abuse protection.
4. **Distributed Task Engine:** local/remote workers, scheduling, leases, retry semantics, and resource-aware execution.
5. **Optional collaboration:** published collections, access roles, comments, and team workspaces.

## 11. Decisions to revisit

- Whether `researchlab.json` uses JSON only or gains YAML support later.
- Which chart rendering/export library is best for a stand-alone publication.
- How environment fingerprints should evolve from best-effort metadata to reproducible containers.
- Whether first publications are static HTML files or static-site bundles with client-side data exploration.

All decisions should be judged against the **first end-to-end experiment → evidence → Research Story** scenario.
