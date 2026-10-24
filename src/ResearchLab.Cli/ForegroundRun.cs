using System.Text;
using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;
using ResearchLab.Execution;
using ResearchLab.Infrastructure.Catalog;
using ResearchLab.Infrastructure.Persistence;

namespace ResearchLab.Cli;

public static partial class ConsoleHost
{
    private static async Task<int> StartAsync(CommandArguments request, SqliteResearchStore store, ProjectCatalog catalog,
        TextWriter output, TextWriter error, TextReader input, CancellationToken cancellationToken)
    {
        var id = new ExperimentId(request.Id!.Value);
        var experiment = await catalog.GetExperimentAsync(id, cancellationToken)
            ?? throw new CatalogException("ExperimentNotFound", "Experiment was not found.");
        var parameters = ExperimentJson.Parameters(request.Parameters, experiment.Definition);
        var review = await catalog.ReviewTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey, cancellationToken);
        await error.WriteLineAsync("Review this trusted local task. Approval applies only to this CLI invocation:");
        await error.WriteLineAsync(JsonSerializer.Serialize(new
        {
            review.ExecutablePath,
            review.Task.Arguments,
            TimeoutSeconds = review.Task.Timeout.TotalSeconds,
            review.Fingerprint,
            Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value.Value)
        }, Json));
        await error.WriteLineAsync("Type yes to trust and run this executable; any other answer or EOF declines:");
        await error.FlushAsync(cancellationToken);
        if (!string.Equals(await ReadApprovalAsync(input, error, cancellationToken), "yes", StringComparison.Ordinal))
        {
            await error.WriteLineAsync("Trust declined. No Run was created.");
            return 5;
        }
        await catalog.ConfirmTaskAsync(experiment.ProjectId, experiment.Definition.TaskKey, review.Fingerprint, cancellationToken);
        var files = new RunLogFiles(request.DataDirectory);
        using var operation = await new RunExecution(store, catalog, new WorkspaceInspector(), new ProcessRunner(files))
            .PrepareRunAsync(id, parameters, cancellationToken);
        // After insertion, Ctrl+C requests owned cancellation. It never cancels the driver or status writes.
        using var registration = cancellationToken.Register(() => operation.RequestCancellation());
        await error.WriteLineAsync($"Run ID: {operation.RunId.Value}");
        await error.FlushAsync(CancellationToken.None);
        var warned = false;
        while (true)
        {
            try { await operation.ExecuteAsync(); break; }
            catch (ProcessCleanupException)
            {
                if (!warned)
                {
                    try
                    {
                        await error.WriteLineAsync("Cleanup is unconfirmed. Keeping the foreground owner and retrying cleanup of the same process; no terminal state is claimed.");
                    }
                    catch (IOException) { } // A closed output pipe must not abandon a live owned process.
                    warned = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }
        var run = await new RunHistory(store).GetRunAsync(operation.RunId);
        var logs = new RunLogs(store, files);
        var paths = await logs.GetPathsAsync(run.Id);
        await output.WriteLineAsync(JsonSerializer.Serialize(Describe(run, paths,
            await ReadLogsAsync(logs, run.Id, paths, CancellationToken.None)), Json));
        return run.Status switch
        {
            RunStatus.Succeeded => 0,
            RunStatus.Canceled => 130,
            RunStatus.TimedOut => 124,
            _ => 6
        };
    }

    private static async ValueTask<string?> ReadApprovalAsync(TextReader input, TextWriter error, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected)
            return await input.ReadLineAsync(cancellationToken);
        // Console.In's synchronized ReadLine can block despite cancellation. Read only
        // available interactive keys so Ctrl+C can cancel preparation without an orphan read task.
        var text = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                await Task.Delay(25, cancellationToken);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                await error.WriteLineAsync();
                return text.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && text.Length > 0)
            {
                text.Length--;
                await error.WriteAsync("\b \b");
            }
            else if (!char.IsControl(key.KeyChar) && text.Length < 128)
            {
                text.Append(key.KeyChar);
                await error.WriteAsync(key.KeyChar);
            }
        }
    }

    private static async Task<object?> ReadLogsAsync(RunLogs logs, RunId id, RunLogPaths? paths, CancellationToken cancellationToken)
    {
        if (paths is null) return null;
        var stdout = await ReadAsync(RunLogStream.Stdout);
        var stderr = await ReadAsync(RunLogStream.Stderr);
        return new { Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated };

        async Task<(string Text, bool Truncated)> ReadAsync(RunLogStream channel)
        {
            await using var stream = await logs.OpenReadAsync(id, channel, cancellationToken);
            using var reader = new StreamReader(stream);
            // Also bound reads of logs modified after capture. JSON escapes terminal control bytes.
            const int limit = 1024 * 1024;
            var buffer = new char[limit + 1];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            return (new string(buffer, 0, Math.Min(count, limit)), count > limit);
        }
    }
}
