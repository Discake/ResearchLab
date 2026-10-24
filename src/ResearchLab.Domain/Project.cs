namespace ResearchLab.Domain;

public sealed class Project
{
    public Project(ProjectId id, string name, string workspacePath, DateTimeOffset createdAt,
        string? description = null, string? gitRemoteUrl = null)
    {
        Guard.Id(id.Value, nameof(id));
        Id = id;
        Name = Guard.Required(name, nameof(name));
        WorkspacePath = Guard.Required(workspacePath, nameof(workspacePath));
        CreatedAtUtc = createdAt.ToUniversalTime();
        Description = description;
        GitRemoteUrl = gitRemoteUrl;
    }

    public ProjectId Id { get; }
    public string Name { get; }
    public string WorkspacePath { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? Description { get; }
    public string? GitRemoteUrl { get; }
}
