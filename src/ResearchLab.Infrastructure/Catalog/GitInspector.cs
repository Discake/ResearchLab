using System.Diagnostics;
using System.Text;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Catalog;

public sealed class GitInspector
{
    private readonly string executable;

    public GitInspector(string? executablePath = null)
    {
        // Resolve from the host PATH, never from the imported repository's working directory.
        executable = executablePath is null ? FindGit() : Path.GetFullPath(executablePath);
    }

    public async Task<(SourceSnapshot Source, IReadOnlyList<string> Diagnostics)> InspectAsync(string workspace, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = await ReadAsync(workspace, ["rev-parse", "--show-toplevel"], cancellationToken);
            if (root.ExitCode != 0 || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Output.Trim())), workspace,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return (new(null, null, null), ["No Git repository rooted at this workspace; provenance is unknown."]);
            var head = await ReadAsync(workspace, ["rev-parse", "--verify", "HEAD"], cancellationToken);
            // status may invoke configured clean/process filters. Disable all of them before
            // inspecting the working tree. Never evaluate repository-defined commands.
            var filters = await ReadAsync(workspace, ["config", "--includes", "--name-only", "--get-regexp", @"^filter\..*\.(clean|smudge|process|required)$"], cancellationToken);
            if (filters.ExitCode is not (0 or 1))
                throw new IOException("Cannot enumerate Git filters safely.");
            var overrides = new List<string>();
            foreach (var key in filters.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(key => key.TrimEnd('\r')))
            {
                if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) || key.Contains('=') || key.Any(char.IsControl))
                    throw new IOException("Unsupported Git filter key.");
                overrides.Add(key + (key.EndsWith(".required", StringComparison.OrdinalIgnoreCase) ? "=false" : "="));
            }
            var status = await ReadAsync(workspace, ["status", "--porcelain=v1", "--untracked-files=normal", "--ignore-submodules=all"], cancellationToken, overrides);
            // Avoid traversing submodule repositories (with their own untrusted configuration).
            // If any gitlink is present, the full dirty state cannot be asserted as clean.
            var tracked = await ReadAsync(workspace, ["ls-files", "--stage"], cancellationToken);
            var hasSubmodules = tracked.ExitCode != 0 || tracked.Output.Split('\n').Any(line => line.StartsWith("160000 ", StringComparison.Ordinal));
            var remote = await ReadAsync(workspace, ["config", "--local", "--get", "remote.origin.url"], cancellationToken);
            return (new(head.ExitCode == 0 ? head.Output.Trim() : null,
                status.ExitCode != 0 ? null : status.Output.Length != 0 ? true : hasSubmodules ? null : false,
                remote.ExitCode == 0 ? SanitizeRemote(remote.Output.Trim()) : null),
                head.ExitCode == 0 && status.ExitCode == 0 && !hasSubmodules && overrides.Count == 0 ? [] :
                ["Git inspection is conservative: filters are disabled, submodules are not traversed, unavailable provenance remains unknown."]);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        { return (new(null, null, null), ["Git metadata could not be read within its execution/output limits."]); }
    }

    private async Task<(int ExitCode, string Output)> ReadAsync(string workspace, string[] arguments, CancellationToken cancellationToken,
        IEnumerable<string>? configOverrides = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var arg in new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-C", workspace })
            start.ArgumentList.Add(arg);
        foreach (var setting in configOverrides ?? [])
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = DrainAsync(process.StandardOutput, timeout);
        var errors = DrainAsync(process.StandardError, timeout);
        try
        {
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            return (process.ExitCode, await output);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); } catch (Exception) { /* Both streams have been observed. */ }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Git inspection exceeded its limits.");
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationTokenSource timeout)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) != 0)
        {
            if (result.Length + count > 65536)
            {
                timeout.Cancel();
                throw new IOException("Git output limit exceeded.");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static string FindGit()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, name)))
                return Path.Combine(directory, name);
        return Path.Combine(AppContext.BaseDirectory, "missing-git", name);
    }

    private static string SanitizeRemote(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh")
            return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return value;
    }
}
