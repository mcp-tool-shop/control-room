namespace ControlRoom.Domain.Model;

public enum ThingKind
{
    LocalScript = 1
}

public enum RunStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Canceled = 4
}

public enum EventKind
{
    RunStarted = 1,
    StdOut = 2,
    StdErr = 3,
    StatusChanged = 4,
    ArtifactAdded = 5,
    RunEnded = 6
}
