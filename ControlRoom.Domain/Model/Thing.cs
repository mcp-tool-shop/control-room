namespace ControlRoom.Domain.Model;

public sealed record Thing(
    ThingId Id,
    string Name,
    ThingKind Kind,
    string ConfigJson,
    DateTimeOffset CreatedAt
);
