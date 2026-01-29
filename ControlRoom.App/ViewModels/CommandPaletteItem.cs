namespace ControlRoom.App.ViewModels;

public sealed record CommandPaletteItem(
    string Title,
    string Hint,
    Func<CancellationToken, Task> Execute,
    int Score
);
