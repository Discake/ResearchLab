# M1 — domain core

Implemented in `ResearchLab.Domain`, using only the .NET standard library. There
are no application use cases, storage, migrations, import, command execution or UI.
The domain can be exercised through its public API and tests; it is not a runnable
author workflow. Fixtures in the tests are explicitly synthetic.

## Identity and parameters

- Project, Experiment and Run IDs are distinct immutable Guid wrappers. Constructors
  and aggregate entry points reject empty IDs, including default struct values.
  Project identity does not derive from its workspace path.
- `ParameterValue.From` accepts exactly Int32, finite Double, Boolean or String.
  It performs no parsing, coercion or culture-dependent conversions. Null, mutable
  objects and unsupported numeric types are rejected with a `DomainException`.
- Names are nonblank and case-sensitive (ordinal). Duplicate definitions and unknown
  input names are rejected. Bounds are inclusive and only valid for numeric types.
  Allowed values must be nonempty, correctly typed and within bounds. Defaults must
  satisfy the same constraints as supplied input.
- A valid default satisfies a missing required parameter. Missing optional parameters
  without defaults remain absent. An explicitly supplied invalid value is never
  replaced by a default. Errors have stable codes and explanatory messages.

## Snapshot ownership

`Run.Create` requires an Experiment, a matching TaskSnapshot and valid parameters.
It freezes the definition, task arguments/timeout, validated parameter values, source
and environment at creation, before any process starts. Collections are copied and
exposed read-only; their elements are immutable. `Experiment.Revise` replaces its
definition without changing earlier Run snapshots.

Source metadata preserves unknown Git SHA/dirty state as null. Environment metadata
is supplied by the caller; Domain does not inspect the machine or infer exact
reproducibility. A TaskSnapshot describes a command and does not grant permission
to execute it. Trusted configuration lookup, executable existence and path containment
checks belong to later application/infrastructure work.

## Lifecycle and evidence

- The approved EN/RU lifecycle includes `Created → Failed` for startup failure.
  `Start` requires a positive process ID; only the future execution service can
  establish that the OS process actually started.
- Success requires exit code zero and explicit confirmation of completed result
  validation. Failure requires a reason; startup failure has no exit code, start
  time or duration. Result-processing failure can retain exit code zero.
- Cancellation before start is valid. Timeout requires the configured duration to
  have elapsed since start. Interruption is available only from Running. Terminal
  statuses cannot be changed, including repeated completion.
- Callers supply timestamps; Domain normalizes them to UTC and rejects backwards
  lifecycle timestamps. Duration is defined only for a started and finished Run.
- Every Run mutation requires its expected `Version` and increments it on success.
  Rejected operations leave state unchanged. This protects sequential stale writes;
  the aggregate is not thread-safe. Application services must serialize mutations,
  and M2 storage must compare versions atomically when saving separate instances.
- Metrics are appended through Run while Running, with finite values, nonblank
  names and optional nonblank units. A series requires exact ordinal unit agreement,
  including agreement on whether a unit is absent; there is no unit conversion.
  Sequence starts at one and follows arrival across the entire Run, independently
  of repeated or unordered Int64 steps and timestamps. Points are immutable.
  Pending output must be drained before terminal completion; failed runs retain
  already accepted points. Protocol parsing and ingestion quotas belong to M4.

## Validation and deferred work

Tests cover parameter constraints, default handling, snapshot ownership, each
exposed status transition, terminal immutability, invalid timestamps/metadata,
stale versions, metric finiteness, unit consistency and sequence order. There is
no public transition back to Created; the status setter is private. The architecture
test permits only the standard library assemblies currently needed by Domain.

Run the restore/build/test/format commands in the repository README. Persistence
concurrency, real process lifecycle, artifact containment and public export security
are intentionally not claimed as implemented or tested by this increment.
