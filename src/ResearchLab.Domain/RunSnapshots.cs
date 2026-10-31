using System.Collections.ObjectModel;

namespace ResearchLab.Domain;

// Describes a task, but neither grants trust nor verifies/starts an executable.
public sealed class TaskSnapshot
{
    public TaskSnapshot(string key, string executable, IEnumerable<string> arguments, TimeSpan timeout)
    {
        Key = Guard.Required(key, nameof(key));
        Executable = Guard.Required(executable, nameof(executable));
        ArgumentNullException.ThrowIfNull(arguments);
        var copy = arguments.ToArray();
        if (copy.Any(argument => argument is null))
            throw new DomainException("InvalidArgument", "Task arguments must not be null.");
        Arguments = Array.AsReadOnly(copy);
        if (timeout <= TimeSpan.Zero)
            throw new DomainException("InvalidTimeout", "Task timeout must be positive.");
        Timeout = timeout;
    }

    public string Key { get; }
    public string Executable { get; }
    public ReadOnlyCollection<string> Arguments { get; }
    public TimeSpan Timeout { get; }
}

public sealed record SourceSnapshot(string? GitHeadSha, bool? IsWorkingTreeDirty, string? RepositoryUrl);

public sealed class EnvironmentSnapshot
{
    public EnvironmentSnapshot(string? operatingSystem = null, string? architecture = null,
        IReadOnlyDictionary<string, string>? toolchains = null)
    {
        OperatingSystem = operatingSystem;
        Architecture = architecture;
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (toolchains is not null)
            foreach (var (name, version) in toolchains)
                copy.Add(Guard.Required(name, "Toolchain name"), Guard.Required(version, "Toolchain version"));
        Toolchains = new ReadOnlyDictionary<string, string>(copy);
    }

    public string? OperatingSystem { get; }
    public string? Architecture { get; }
    public IReadOnlyDictionary<string, string> Toolchains { get; }
}

public sealed record ExperimentSnapshot(ExperimentId ExperimentId, ProjectId ProjectId,
    ExperimentDefinition Definition, TaskSnapshot Task);
