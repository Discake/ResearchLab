using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;

namespace ResearchLab.Cli;

public static partial class ConsoleHost
{
    public const string Usage = """
        ResearchLab local console host
        researchlab --database <sqlite-file> --data-dir <log-root> database init
        researchlab --database <sqlite-file> --data-dir <log-root> project import <path> --name <name>
        researchlab --database <sqlite-file> --data-dir <log-root> experiment create --project <project-id> --definition <json-file>
        researchlab --database <sqlite-file> --data-dir <log-root> project list
        researchlab --database <sqlite-file> --data-dir <log-root> experiment list --project <project-id>
        researchlab --database <sqlite-file> --data-dir <log-root> run list --experiment <experiment-id> [--offset <n>] [--page-size <n>]
        researchlab --database <sqlite-file> --data-dir <log-root> run show <run-id>
        researchlab --database <sqlite-file> --data-dir <log-root> run metrics <run-id> [--name <metric>] [--offset <n>] [--page-size <n>]
        researchlab --database <sqlite-file> --data-dir <log-root> run show <run-id> --include-logs
        researchlab --database <sqlite-file> --data-dir <log-root> run start <experiment-id> [--parameters <json-object>]
        researchlab --database <sqlite-file> --data-dir <log-root> run recover
        researchlab --help
        Both paths are explicit; relative paths resolve against the current directory.
        --data-dir is private storage; start creates bounded logs and <RunId:N>/output here.
        CWD remains the workspace; programs must use RESEARCHLAB_OUTPUT_DIR for output files.
        Run pages use Application defaults and limits (50 by default, maximum 100).
        Start displays a trust review on stderr; type yes on stdin to approve this invocation only.
        Output: JSON on stdout; review/progress/errors on stderr. Only database init applies migrations. No automatic recovery.
        Exit codes: 0 success, 1 unexpected error, 2 usage/validation, 3 not found, 4 storage/data error,
        5 trust declined/changed, 6 Run failed, 124 Run timed out, 130 canceled.
        """;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default, TextReader? input = null)
    {
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        CommandArguments request;
        try { request = CommandArguments.Parse(args); }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync("Use researchlab --help for usage.");
            return 2;
        }
        try
        {
            if (File.Exists(request.DataDirectory))
                throw new IOException("Configured data directory is not a directory.");
            var readOnly = request.Command is ConsoleCommand.ProjectList or ConsoleCommand.ExperimentList or ConsoleCommand.RunList or ConsoleCommand.RunShow or ConsoleCommand.RunMetrics;
            var store = new SqliteResearchStore(request.DatabasePath, readOnly: readOnly,
                createIfMissing: request.Command == ConsoleCommand.DatabaseInit);
            var catalog = new ProjectCatalog(store, new WorkspaceInspector());
            var history = new RunHistory(store);
            object result;
            switch (request.Command)
            {
                case ConsoleCommand.DatabaseInit:
                    await store.InitializeAsync(cancellationToken);
                    result = new { Initialized = true };
                    break;
                case ConsoleCommand.ProjectImport:
                    var imported = await catalog.ImportProjectAsync(request.Workspace!, request.Name!, cancellationToken);
                    result = new { Id = imported.Project.Id.Value, imported.Project.Name, imported.Diagnostics };
                    break;
                case ConsoleCommand.ExperimentCreate:
                    var definition = ExperimentJson.Definition(await File.ReadAllTextAsync(request.Definition!, cancellationToken));
                    var created = await catalog.CreateExperimentAsync(new ProjectId(request.Id!.Value), definition, cancellationToken);
                    result = new { Id = created.Id.Value, ProjectId = created.ProjectId.Value, created.Definition.Name };
                    break;
                case ConsoleCommand.RunStart:
                    return await StartAsync(request, store, catalog, output, error, input ?? Console.In, cancellationToken);
                case ConsoleCommand.RunRecover:
                    await error.WriteLineAsync("Warning: Interrupted records verified owner loss; it does not prove child process termination. Recovery never kills saved PIDs. Active or unverified owners remain unchanged.");
                    var recovery = await new RunRecovery(store).RecoverAsync(cancellationToken);
                    result = new
                    {
                        Recovered = recovery.Recovered.Select(id => id.Value).ToArray(),
                        Conflicts = recovery.Conflicts.Select(id => id.Value).ToArray(),
                        recovery.UnverifiedSessions
                    };
                    break;
                case ConsoleCommand.ProjectList:
                    result = (await catalog.ListProjectsAsync(cancellationToken)).Select(project => new
                    {
                        Id = project.Id.Value,
                        project.Name,
                        project.Description,
                        project.CreatedAtUtc
                    }).ToArray();
                    break;
                case ConsoleCommand.ExperimentList:
                    var projectId = new ProjectId(request.Id!.Value);
                    await catalog.GetProjectAsync(projectId, cancellationToken);
                    result = (await catalog.ListExperimentsAsync(projectId, cancellationToken)).Select(experiment => new
                    {
                        Id = experiment.Id.Value,
                        ProjectId = experiment.ProjectId.Value,
                        experiment.Definition.Name,
                        experiment.Definition.Description,
                        experiment.Definition.TaskKey,
                        experiment.CreatedAtUtc,
                        experiment.UpdatedAtUtc
                    }).ToArray();
                    break;
                case ConsoleCommand.RunList:
                    result = (await history.ListRunsAsync(new ExperimentId(request.Id!.Value), request.Page, cancellationToken))
                        .Select(run => Describe(run)).ToArray();
                    break;
                case ConsoleCommand.RunMetrics:
                    var metrics = await ((IRunMetricReader)store).GetMetricsAsync(new RunId(request.Id!.Value), request.MetricName, request.Page, cancellationToken);
                    result = new { RunId = metrics.RunId.Value, metrics.Name, metrics.Offset, metrics.PageSize, metrics.HasMore, metrics.Points };
                    break;
                case ConsoleCommand.RunShow:
                    var details = await history.GetRunAsync(new RunId(request.Id!.Value), cancellationToken);
                    var logs = new RunLogs(store, new RunLogFiles(request.DataDirectory));
                    var paths = await logs.GetPathsAsync(details.Id, cancellationToken);
                    result = Describe(details, paths, request.IncludeLogs ? await ReadLogsAsync(logs, details.Id, paths, cancellationToken) : null);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported command.");
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result, Json).AsMemory(), cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Operation canceled before active execution completed.");
            return 130;
        }
        catch (CatalogException exception) when (exception.Code is "ProjectNotFound" or "ExperimentNotFound" or "RunNotFound")
        {
            await error.WriteLineAsync($"{exception.Code}: requested record was not found.");
            return 3;
        }
        catch (CatalogException exception) when (exception.Code is "TaskNotTrusted" or "TaskChanged" or "PreparationChanged")
        {
            await error.WriteLineAsync("The reviewed task is no longer trusted or changed; review it in a new invocation.");
            return 5;
        }
        catch (CatalogException exception)
        {
            await error.WriteLineAsync($"Catalog or execution storage error: {exception.Code}.");
            return exception.Code == "ProjectAlreadyImported" ? 2 : 4;
        }
        catch (DomainException exception) when (request.Command is ConsoleCommand.RunStart or ConsoleCommand.ExperimentCreate)
        {
            await error.WriteLineAsync($"Validation failed: {exception.Code}.");
            return 2;
        }
        catch (Exception exception) when ((exception is ArgumentException or JsonException) &&
            (request.Command is ConsoleCommand.RunStart or ConsoleCommand.ExperimentCreate))
        {
            await error.WriteLineAsync("Invalid JSON input or experiment parameters. Use --help and the foreground-run guide.");
            return 2;
        }
        catch (Exception exception) when (exception is SqliteException or DbUpdateException or IOException or UnauthorizedAccessException or JsonException or DomainException)
        {
            await error.WriteLineAsync("Cannot read configured storage or save changes. Check access and a compatible ResearchLab database/data directory; use database init explicitly for initialization/migrations.");
            return 4;
        }
        catch (Exception)
        {
            // No exception dumps, connection strings, environment or arbitrary stored text.
            await error.WriteLineAsync("The operation failed unexpectedly.");
            return 1;
        }
    }

    private static object Describe(RunDetails run, RunLogPaths? logs = null, object? logOutput = null) => new
    {
        Id = run.Id.Value,
        ExperimentId = run.ExperimentId.Value,
        Status = run.Status.ToString(),
        run.CreatedAtUtc,
        run.StartedAtUtc,
        run.FinishedAtUtc,
        Parameters = run.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Value),
        run.ExitCode,
        run.Diagnostic,
        Logs = logs,
        LogOutput = logOutput
    };
}
