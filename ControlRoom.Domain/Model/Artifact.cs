namespace ControlRoom.Domain.Model;

public sealed record Artifact(
    ArtifactId Id,
    RunId RunId,
    string MediaType,
    string Locator,
    string? Sha256Hex,
    DateTimeOffset CreatedAt
);
