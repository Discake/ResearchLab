using System.Collections.ObjectModel;
using System.Globalization;
using ResearchLab.Domain;

namespace ResearchLab.Application;

public sealed class RunPageRequest
{
    public const int MaximumPageSize = 100;

    public RunPageRequest(int offset = 0, int pageSize = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pageSize is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 100.");
        Offset = offset;
        PageSize = pageSize;
    }

    public int Offset { get; }
    public int PageSize { get; }
}

// No mutable aggregate, executable/workspace paths, environment or raw exception text.
public sealed record RunDetails(RunId Id, ExperimentId ExperimentId, RunStatus Status,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc,
    IReadOnlyDictionary<string, ParameterValue> Parameters, int? ExitCode, string? Diagnostic);

public sealed class RunHistory(IResearchStore store)
{
    public async Task<RunDetails> GetRunAsync(RunId id, CancellationToken cancellationToken = default)
    {
        var run = await store.GetRunAsync(id, cancellationToken)
            ?? throw new CatalogException("RunNotFound", "Run was not found.");
        return Describe(run);
    }

    public async Task<IReadOnlyList<RunDetails>> ListRunsAsync(ExperimentId experimentId,
        RunPageRequest? page = null, CancellationToken cancellationToken = default)
    {
        if (await store.GetExperimentAsync(experimentId, cancellationToken) is null)
            throw new CatalogException("ExperimentNotFound", "Experiment was not found.");
        var runs = await store.ListRunsAsync(experimentId, page ?? new RunPageRequest(), cancellationToken);
        return Array.AsReadOnly(runs.Select(Describe).ToArray());
    }

    private static RunDetails Describe(Run run) => new(run.Id, run.ExperimentId, run.Status,
        run.CreatedAtUtc, run.StartedAtUtc, run.FinishedAtUtc,
        new ReadOnlyDictionary<string, ParameterValue>(run.ParametersSnapshot.ToDictionary(pair => pair.Key, pair => pair.Value)),
        run.ExitCode, run.Status switch
        {
            RunStatus.Failed when run.StartedAtUtc is null => "Run failed before process startup.",
            RunStatus.Failed when run.ExitCode is not null and not 0 =>
                $"Run failed; process exit code {run.ExitCode.Value.ToString(CultureInfo.InvariantCulture)}.",
            RunStatus.Failed => "Process execution or result collection failed.",
            RunStatus.Canceled => "Run was canceled.",
            RunStatus.TimedOut => "Configured execution timeout exceeded.",
            RunStatus.Interrupted => "Run was interrupted before completion was recorded.",
            _ => null
        });
}
