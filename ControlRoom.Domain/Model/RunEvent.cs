namespace ControlRoom.Domain.Model;

public sealed record RunEvent(
    long Seq,
    RunId RunId,
    DateTimeOffset At,
    EventKind Kind,
    string PayloadJson
);
