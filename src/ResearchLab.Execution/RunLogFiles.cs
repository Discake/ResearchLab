using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Execution;

// Run-local filesystem allocation; this is not an artifact registration/storage service.
public sealed class RunLogFiles : IRunLogFiles
{
    private readonly string root;

    public RunLogFiles(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        root = Path.GetFullPath(storageRoot);
    }

    internal (FileStream Stdout, FileStream Stderr) Create(RunId runId)
    {
        var directory = DirectoryPath(runId);
        CheckPath(root);
        Directory.CreateDirectory(root);
        CheckPath(root);
        // Existing attempts are never overwritten, including partial allocation failures.
        if (Path.Exists(directory))
            throw new IOException("Run log directory already exists.");
        Directory.CreateDirectory(directory);
        CheckPath(directory);
        var stdout = CreateFile(Path.Combine(directory, "stdout.log"));
        try { return (stdout, CreateFile(Path.Combine(directory, "stderr.log"))); }
        catch { stdout.Dispose(); throw; }
    }

    public RunLogPaths? GetPaths(RunId runId)
    {
        var directory = DirectoryPath(runId);
        CheckPath(directory);
        if (!Directory.Exists(directory))
            return null;
        CheckPath(Path.Combine(directory, "stdout.log"));
        CheckPath(Path.Combine(directory, "stderr.log"));
        return new(Path.Combine(runId.Value.ToString("N"), "stdout.log"), Path.Combine(runId.Value.ToString("N"), "stderr.log"));
    }

    // Called only after Create has exclusively reserved the attempt's log files.
    internal string CreateOutputDirectory(RunId runId)
    {
        var directory = Path.Combine(DirectoryPath(runId), "output");
        CheckPath(directory);
        if (Path.Exists(directory))
            throw new IOException("Run output directory already exists.");
        Directory.CreateDirectory(directory);
        CheckPath(directory);
        return directory;
    }

    public Stream OpenRead(RunId runId, RunLogStream stream)
    {
        var name = stream switch
        {
            RunLogStream.Stdout => "stdout.log",
            RunLogStream.Stderr => "stderr.log",
            _ => throw new ArgumentOutOfRangeException(nameof(stream))
        };
        var path = Path.Combine(DirectoryPath(runId), name);
        CheckPath(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous);
    }

    private string DirectoryPath(RunId runId)
    {
        if (runId.Value == Guid.Empty)
            throw new ArgumentException("A persisted Run ID is required.", nameof(runId));
        return Path.Combine(root, runId.Value.ToString("N"));
    }

    private static FileStream CreateFile(string path)
    {
        CheckPath(path);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous);
    }

    // Inspect every existing component, including ancestors of the configured root.
    // Reject dangling links too (File.Exists/Directory.Exists alone miss those).
    private static void CheckPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Links are not allowed in run log paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
