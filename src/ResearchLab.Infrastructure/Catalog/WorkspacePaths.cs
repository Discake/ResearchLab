using ResearchLab.Application;

namespace ResearchLab.Infrastructure.Catalog;

internal static class WorkspacePaths
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Workspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new CatalogException("InvalidWorkspace", "Workspace path must not be blank.");
        return Canonicalize(path, directory: true);
    }

    public static string FileInside(string workspace, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.StartsWith('\\') || relativePath.StartsWith('/'))
            throw new CatalogException("UnsafePath", "Task/configuration paths must be relative to the workspace.");
        var path = Path.GetFullPath(Path.Combine(workspace, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
        EnsureInside(workspace, path);
        var resolved = Canonicalize(path, directory: false);
        EnsureInside(workspace, resolved);
        return resolved;
    }

    private static void EnsureInside(string workspace, string path)
    {
        var prefix = Path.EndsInDirectorySeparator(workspace) ? workspace : workspace + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, Comparison))
            throw new CatalogException("UnsafePath", "Path escapes the selected workspace.");
    }

    private static string Canonicalize(string path, bool directory, int depth = 0)
    {
        if (depth > 40)
            throw new CatalogException("UnsafePath", "Too many symbolic link indirections.");
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            var current = root;
            foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (!info.Exists)
                    throw new CatalogException("PathNotFound", "Workspace/configuration/executable path does not exist.");
                if (info.LinkTarget is not null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new CatalogException("UnsafePath", "Unable to resolve symbolic link.");
                    current = Canonicalize(target, Directory.Exists(target), depth + 1);
                }
            }
            if (directory ? !Directory.Exists(current) : !File.Exists(current))
                throw new CatalogException("InvalidPathKind", directory ? "Workspace must be a directory." : "Expected a regular file.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new CatalogException("InvalidPath", "The local path cannot be resolved safely."); }
    }
}
