namespace ResearchLab.Domain;

public enum RunStatus
{
    Created,
    Running,
    Succeeded,
    Failed,
    Canceled,
    TimedOut,
    Interrupted
}
