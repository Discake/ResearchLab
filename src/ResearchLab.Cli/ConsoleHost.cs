using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;

namespace ResearchLab.Cli;

public static class ConsoleHost
{
    public const string Usage = """
        ResearchLab read-only console host
        researchlab --database <sqlite-file> --data-dir <log-root> project list
        researchlab --database <sqlite-file> --data-dir <log-root> experiment list --project <project-id>
        researchlab --database <sqlite-file> --data-dir <log-root> run list --experiment <experiment-id> [--offset <n>] [--page-size <n>]
        researchlab --database <sqlite-file> --data-dir <log-root> run show <run-id>
        researchlab --help
        Both paths are explicit; relative paths resolve against the current directory.
        --data-dir is the existing private log root used by RunLogFiles.
        Run pages use Application defaults and limits (50 by default, maximum 100).
        Output: JSON on stdout; errors on stderr. No initialization, migrations or recovery.
        Exit codes: 0 success, 1 unexpected error, 2 usage, 3 not found, 4 storage/data error, 130 canceled.
        """;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
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
            // Composition only. Never initialize/migrate, acquire an owner session or execute.
            var store = new SqliteResearchStore(request.DatabasePath, readOnly: true);
            var catalog = new ProjectCatalog(store, new WorkspaceInspector());
            var history = new RunHistory(store);
            object result;
            switch (request.Command)
            {
                case ReadCommand.ProjectList:
                    result = (await catalog.ListProjectsAsync(cancellationToken)).Select(project => new
                    {
                        Id = project.Id.Value,
                        project.Name,
                        project.Description,
                        project.CreatedAtUtc
                    }).ToArray();
                    break;
                case ReadCommand.ExperimentList:
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
                case ReadCommand.RunList:
                    result = (await history.ListRunsAsync(new ExperimentId(request.Id!.Value), request.Page, cancellationToken))
                        .Select(run => Describe(run)).ToArray();
                    break;
                case ReadCommand.RunShow:
                    var details = await history.GetRunAsync(new RunId(request.Id!.Value), cancellationToken);
                    var logs = new RunLogs(store, new RunLogFiles(request.DataDirectory));
                    result = Describe(details, await logs.GetPathsAsync(details.Id, cancellationToken));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported command.");
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result, Json).AsMemory(), cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Read canceled.");
            return 130;
        }
        catch (CatalogException exception) when (exception.Code is "ProjectNotFound" or "ExperimentNotFound" or "RunNotFound")
        {
            await error.WriteLineAsync($"{exception.Code}: requested record was not found.");
            return 3;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or JsonException or DomainException)
        {
            await error.WriteLineAsync("Cannot read configured storage. Check access and an existing compatible ResearchLab database/data directory.");
            return 4;
        }
        catch (Exception)
        {
            // No exception dumps, connection strings, environment or arbitrary stored text.
            await error.WriteLineAsync("The read failed unexpectedly.");
            return 1;
        }
    }

    private static object Describe(RunDetails run, RunLogPaths? logs = null) => new
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
        Logs = logs
    };
}
