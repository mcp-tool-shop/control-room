namespace ControlRoom.Domain.Model;

public readonly record struct ThingId(Guid Value)
{
    public static ThingId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct RunId(Guid Value)
{
    public static RunId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ArtifactId(Guid Value)
{
    public static ArtifactId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}
