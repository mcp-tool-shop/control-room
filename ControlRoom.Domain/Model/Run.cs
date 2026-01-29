namespace ControlRoom.Domain.Model;

public sealed record Run(
    RunId Id,
    ThingId ThingId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    RunStatus Status,
    int? ExitCode,
    string? Summary
);
