using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResearchLab.Application;
using ResearchLab.Cli;
using ResearchLab.Domain;
using ResearchLab.Infrastructure.Persistence;
using Xunit;

namespace ResearchLab.IntegrationTests;

public sealed class ForegroundCliTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NativeCliFromEmptyStorageRunsDemoAndReopensResultWithoutPersistingTrust()
    {
        using var fixture = new TestWorkspace();
        var id = await SetupAsync(fixture, native: true);
        var success = await NativeAsync(fixture, "yes\n", "run", "start", id.ToString(), "--parameters",
            "{\"iterations\":4,\"scale\":2,\"label\":\"Native demo\",\"verbose\":true}");
        Assert.Equal(0, success.Code);
        Assert.Contains("fingerprint", success.Error);
        Assert.Contains("Run ID:", success.Error);
        Guid runId;
        using (var json = JsonDocument.Parse(success.Output))
        {
            var run = json.RootElement;
            runId = run.GetProperty("id").GetGuid();
            Assert.Equal("Succeeded", run.GetProperty("status").GetString());
            Assert.Equal(0, run.GetProperty("exitCode").GetInt32());
            Assert.Equal(4, run.GetProperty("parameters").GetProperty("iterations").GetInt32());
            Assert.Equal(2, run.GetProperty("parameters").GetProperty("scale").GetDouble());
            Assert.Contains("Sum: 20", run.GetProperty("logOutput").GetProperty("stdout").GetString()!);
            Assert.Contains("Demo diagnostic", run.GetProperty("logOutput").GetProperty("stderr").GetString()!);
        }
        var show = await NativeAsync(fixture, "", "run", "show", runId.ToString(), "--include-logs");
        Assert.Equal(0, show.Code);
        using (var json = JsonDocument.Parse(show.Output))
        {
            Assert.Equal(runId, json.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("Succeeded", json.RootElement.GetProperty("status").GetString());
            Assert.Contains("Native demo", json.RootElement.GetProperty("logOutput").GetProperty("stdout").GetString()!);
        }
        var declined = await NativeAsync(fixture, "", "run", "start", id.ToString());
        Assert.Equal(5, declined.Code);
        Assert.Empty(declined.Output);
        Assert.Contains("Type yes", declined.Error);
        Assert.Single(await fixture.Store.ListRunsAsync(new ExperimentId(id), new RunPageRequest(), Token));
    }

    [Theory]
    [InlineData("{\"fail\":true}", 6, RunStatus.Failed, 7)]
    [InlineData("{\"delayMilliseconds\":30000}", 124, RunStatus.TimedOut, null)]
    public async Task RealFailureAndTimeoutDisplayAndPersistDistinctOutcomes(string parameters, int code, RunStatus status, int? exitCode)
    {
        using var fixture = new TestWorkspace();
        var id = await SetupAsync(fixture);
        if (status == RunStatus.TimedOut) WriteConfiguration(fixture, timeout: 1);
        var result = await InvokeAsync(fixture, "yes\n", Token, "run", "start", id.ToString(), "--parameters", parameters);
        Assert.Equal(code, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var saved = (await fixture.Store.GetRunAsync(new RunId(json.RootElement.GetProperty("id").GetGuid()), Token))!;
        Assert.Equal(status, saved.Status);
        Assert.Equal(exitCode, saved.ExitCode);
        Assert.NotNull(saved.FinishedAtUtc);
        Assert.NotNull(json.RootElement.GetProperty("diagnostic").GetString());
        Assert.Contains("Demo diagnostic", json.RootElement.GetProperty("logOutput").GetProperty("stderr").GetString()!);
    }

    [Fact]
    public async Task WindowsConsoleCtrlCThroughNativeEntryPointPersistsCanceledBeforeExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TestWorkspace();
        var id = await SetupAsync(fixture);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ResearchLab.ProcessFixture.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "interrupt-cli", typeof(ConsoleHost).Assembly.Location, fixture.Database,
                Path.Combine(fixture.Root, "logs"), id.ToString() }
        };
        using var helper = Process.Start(start)!;
        var stdout = helper.StandardOutput.ReadToEndAsync(Token);
        var stderr = helper.StandardError.ReadToEndAsync(Token);
        try { await helper.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(25), Token); }
        finally
        {
            if (!helper.HasExited) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(Token); }
        }
        Assert.True(helper.ExitCode == 0, await stderr);
        using var result = JsonDocument.Parse(await stdout);
        Assert.Equal(130, result.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.True(result.RootElement.GetProperty("ChildExited").GetBoolean());
        using var run = JsonDocument.Parse(result.RootElement.GetProperty("Stdout").GetString()!);
        Assert.Equal("Canceled", run.RootElement.GetProperty("status").GetString());
        var saved = Assert.Single(await fixture.Store.ListRunsAsync(new ExperimentId(id), new RunPageRequest(), Token));
        Assert.Equal(RunStatus.Canceled, saved.Status);
        Assert.NotNull(saved.FinishedAtUtc);
    }

    [Fact]
    public async Task CtrlCTokenRequestsOwnedCancellationAndWaitsForTerminalPersistence()
    {
        using var fixture = new TestWorkspace();
        var id = await SetupAsync(fixture);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var foreground = InvokeAsync(fixture, "yes\n", interrupt.Token, "run", "start", id.ToString(), "--parameters",
            "{\"delayMilliseconds\":30000}");
        Run? running = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (running is null)
        {
            running = (await fixture.Store.ListRunsAsync(new ExperimentId(id), new RunPageRequest(), limit.Token))
                .SingleOrDefault(run => run.Status == RunStatus.Running);
            if (running is null) await Task.Delay(10, limit.Token);
        }
        using var process = Process.GetProcessById(running.ProcessId!.Value);
        try
        {
            interrupt.Cancel(); // Program's Console.CancelKeyPress handler cancels this same request token.
            var result = await foreground.WaitAsync(TimeSpan.FromSeconds(12), Token);
            Assert.Equal(130, result.Code);
            Assert.True(process.HasExited);
            var saved = (await fixture.Store.GetRunAsync(running.Id, Token))!;
            Assert.Equal(RunStatus.Canceled, saved.Status);
            Assert.Equal(2, saved.Version);
            Assert.Null(saved.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal("Canceled", json.RootElement.GetProperty("status").GetString());
            Assert.Empty((await new RunRecovery(fixture.Store).RecoverAsync(Token)).Recovered);
        }
        finally
        {
            interrupt.Cancel();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await foreground;
        }
    }

    [Theory]
    [InlineData("no\n")]
    [InlineData("")]
    public async Task DeclinedOrMissingApprovalDoesNotCreateRunOrLogs(string response)
    {
        using var fixture = new TestWorkspace();
        var id = await SetupAsync(fixture);
        var result = await InvokeAsync(fixture, response, Token, "run", "start", id.ToString());
        Assert.Equal(5, result.Code);
        Assert.Empty(await fixture.Store.ListRunsAsync(new ExperimentId(id), new RunPageRequest(), Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "logs")));
        Assert.False(Directory.Exists(fixture.Database + ".sessions"));
    }

    [Theory]
    [InlineData("{\"iterations\":0}")]
    [InlineData("{\"iterations\":1.5}")]
    [InlineData("{\"scale\":1e9999}")]
    [InlineData("{\"verbose\":\"true\"}")]
    [InlineData("{\"label\":null}")]
    [InlineData("{\"unknown\":1}")]
    [InlineData("{\"iterations\":2,\"iterations\":3}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task InvalidParametersFailBeforeTrustAndRunCreation(string parameters)
    {
        using var fixture = new TestWorkspace();
        var id = await SetupAsync(fixture);
        var result = await InvokeAsync(fixture, "yes\n", Token, "run", "start", id.ToString(), "--parameters", parameters);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.DoesNotContain("Type yes", result.Error);
        Assert.Empty(await fixture.Store.ListRunsAsync(new ExperimentId(id), new RunPageRequest(), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskOrExecutableChangedAfterDisplayedReviewIsNotRun(bool executableChanged)
    {
        using var fixture = new TestWorkspace();
        var id = await SetupAsync(fixture);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        using var input = new ChangingInput(() =>
        {
            if (executableChanged)
                File.AppendAllText(Path.Combine(fixture.Workspace, OperatingSystem.IsWindows() ? "researchlab-demo.exe" : "researchlab-demo"), "synthetic changed bytes");
            else
                WriteConfiguration(fixture, timeout: 11);
        });
        var result = await ConsoleHost.RunAsync(Arguments(fixture, "run", "start", id.ToString()), output, error, Token, input);
        Assert.Equal(5, result);
        Assert.Empty(output.ToString());
        Assert.Empty(await fixture.Store.ListRunsAsync(new ExperimentId(id), new RunPageRequest(), Token));
    }

    [Fact]
    public async Task MissingDatabaseForStartDoesNotCreateStorage()
    {
        using var fixture = new TestWorkspace();
        var result = await InvokeAsync(fixture, "yes\n", Token, "run", "start", Guid.NewGuid().ToString());
        Assert.Equal(4, result.Code);
        Assert.False(File.Exists(fixture.Database));
    }

    private static async Task<Guid> SetupAsync(TestWorkspace fixture, bool native = false)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ForegroundDemo")))
        {
            var target = Path.Combine(fixture.Workspace, Path.GetFileName(file));
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows() && Path.GetFileName(file) == "researchlab-demo")
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
        WriteConfiguration(fixture);
        async Task<Guid> Create(string[] command)
        {
            var result = native ? await NativeAsync(fixture, "", command) : await InvokeAsync(fixture, "", Token, command);
            Assert.True(result.Code == 0, result.Error);
            using var json = JsonDocument.Parse(result.Output);
            return json.RootElement.TryGetProperty("id", out var id) ? id.GetGuid() : Guid.Empty;
        }
        await Create(["database", "init"]);
        var project = await Create(["project", "import", fixture.Workspace, "--name", "CLI demo"]);
        return await Create(["experiment", "create", "--project", project.ToString(), "--definition", Path.Combine(fixture.Workspace, "experiment.json")]);
    }

    private static void WriteConfiguration(TestWorkspace fixture, int timeout = 10) =>
        fixture.WriteConfiguration(OperatingSystem.IsWindows() ? "researchlab-demo.exe" : "researchlab-demo", [], timeout);

    private static string[] Arguments(TestWorkspace fixture, params string[] command) =>
        ["--database", fixture.Database, "--data-dir", Path.Combine(fixture.Root, "logs"), .. command];

    private static async Task<(int Code, string Output, string Error)> InvokeAsync(TestWorkspace fixture, string response,
        CancellationToken token, params string[] command)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        using var input = new StringReader(response);
        var code = await ConsoleHost.RunAsync(Arguments(fixture, command), output, error, token, input);
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<(int Code, string Output, string Error)> NativeAsync(TestWorkspace fixture, string response, params string[] command)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(ConsoleHost).Assembly.Location);
        foreach (var arg in Arguments(fixture, command)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.StandardInput.WriteAsync(response.AsMemory(), Token);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(20), Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(Token); }
        }
        return (process.ExitCode, await output, await error);
    }

    private sealed class ChangingInput(Action change) : StringReader("yes\n")
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            change();
            return base.ReadLineAsync(cancellationToken);
        }
    }
}
