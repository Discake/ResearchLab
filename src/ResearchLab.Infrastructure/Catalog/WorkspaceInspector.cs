using System.Security.Cryptography;
using System.Text;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Catalog;

public sealed class WorkspaceInspector(GitInspector? gitInspector = null) : IWorkspaceInspector
{
    private readonly GitInspector git = gitInspector ?? new();

    public async Task<WorkspaceInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var (workspace, tasks, digest) = await ReadConfigurationAsync(path, cancellationToken);
        var (source, diagnostics) = await git.InspectAsync(workspace, cancellationToken);
        return new(workspace, tasks, digest, source, diagnostics);
    }

    public async Task<TaskReview> ReviewTaskAsync(Project project, string taskKey, CancellationToken cancellationToken = default)
    {
        var (workspace, tasks, digest) = await ReadConfigurationAsync(project.WorkspacePath, cancellationToken);
        if (!tasks.TryGetValue(taskKey, out var task))
            throw new CatalogException("TaskNotFound", "Task is not declared in researchlab.json.");
        var executable = WorkspacePaths.FileInside(workspace, task.Executable);
        await using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            size += count;
            if (size > 256L * 1024 * 1024)
                throw new CatalogException("ExecutableTooLarge", "Task review supports executables up to 256 MiB.");
            hash.AppendData(buffer, 0, count);
        }
        var contentHash = Convert.ToHexString(hash.GetHashAndReset());
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{project.Id.Value}\n{workspace}\n{taskKey}\n{digest}\n{executable}\n{contentHash}")));
        return new(project.Id, task, executable, fingerprint);
    }

    private static async Task<(string Workspace, IReadOnlyDictionary<string, TaskSnapshot> Tasks, string Digest)> ReadConfigurationAsync(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = WorkspacePaths.Workspace(path);
        var configuration = WorkspacePaths.FileInside(workspace, "researchlab.json");
        await using var stream = new FileStream(configuration, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var buffer = new byte[1024 * 1024 + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (count == 0)
                break;
            length += count;
        }
        if (length == buffer.Length)
            throw new CatalogException("ConfigurationTooLarge", "researchlab.json exceeds 1 MiB.");
        var bytes = buffer[..length];
        var tasks = TaskConfiguration.Parse(bytes);
        foreach (var task in tasks.Values)
            WorkspacePaths.FileInside(workspace, task.Executable);
        return (workspace, tasks, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
