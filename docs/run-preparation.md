# M3.1a — Run preparation

`new RunPreparation(catalog).PrepareAsync(experimentId, parameters, cancellationToken)`
uses the same `ProjectCatalog` instance that holds the owner's session-local task
consent. It loads Experiment and Project, validates parameters using Domain rules
(including defaults and omitted optional values), then calls `GetTrustedTaskAsync`.
Existing configuration, executable containment, symlink and fingerprint checks apply.

The immutable `PreparedRun` contains the experiment/task definition, copied validated
parameters, working directory, resolved executable path, trust fingerprint, preparation
time and available OS/process architecture metadata. Arguments stay literal; parameters
are separate data, without interpolation into commands. No environment secrets are read.

Missing objects use `CatalogException` codes `ExperimentNotFound` / `ProjectNotFound`.
Trust/path/configuration errors retain their existing catalog codes; invalid parameters
retain the descriptive `DomainException` codes and messages. No new error hierarchy,
dependencies, persistence schema or M0–M2 contract changes are introduced.

Preparation launches no process (including Git). Source metadata is explicitly unknown:
all `SourceSnapshot` fields are null, with no claim of reproducibility. It neither
creates/persists a Run nor changes lifecycle state, and allocates no output directory.
This is prepared input for a future runner, not a persisted `Created` Run.

The future execution step must capture available source metadata, persist a new Run
using the frozen definitions/parameters, and revalidate the current task fingerprint
against this snapshot immediately before launch. Preparation is not a reusable grant
of execution permission and does not eliminate filesystem races. Process startup,
permissions, output directory, cancellation, history and recovery remain out of scope.
