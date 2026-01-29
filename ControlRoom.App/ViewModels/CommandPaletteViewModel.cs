using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ThingQueries _things;
    private readonly RunQueries _runs;
    private readonly RunLocalScript _runScript;

    private readonly List<Func<IEnumerable<CommandPaletteItem>>> _providers = [];

    public event Action? Opened;

    public CommandPaletteViewModel(ThingQueries things, RunQueries runs, RunLocalScript runScript)
    {
        _things = things;
        _runs = runs;
        _runScript = runScript;

        // Providers build items on demand (fast enough for local DB)
        _providers.Add(BuildCoreCommands);
        _providers.Add(BuildSmartFailureCommands);
        _providers.Add(BuildThingActions);
        _providers.Add(BuildRecentRunActions);

        RefreshItems();
    }

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private string query = "";

    public ObservableCollection<CommandPaletteItem> Items { get; } = [];

    [ObservableProperty]
    private CommandPaletteItem? selectedItem;

    partial void OnQueryChanged(string value) => RefreshItems();

    public void Show()
    {
        IsOpen = true;
        Query = "";
        RefreshItems();
        SelectedItem = Items.FirstOrDefault();
        Opened?.Invoke();
    }

    public void Toggle()
    {
        if (IsOpen)
            IsOpen = false;
        else
            Show();
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
    }

    [RelayCommand]
    private void Noop() { /* prevents backdrop click from bubbling */ }

    [RelayCommand]
    private async Task ExecuteSelectedAsync(CancellationToken ct)
    {
        var item = SelectedItem ?? Items.FirstOrDefault();
        if (item is null) return;

        IsOpen = false;
        await item.Execute(ct);
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (Items.Count == 0) return;
        var idx = SelectedItem is null ? 0 : Items.IndexOf(SelectedItem);
        idx = Math.Min(idx + 1, Items.Count - 1);
        SelectedItem = Items[idx];
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (Items.Count == 0) return;
        var idx = SelectedItem is null ? 0 : Items.IndexOf(SelectedItem);
        idx = Math.Max(idx - 1, 0);
        SelectedItem = Items[idx];
    }

    private void RefreshItems()
    {
        var all = _providers.SelectMany(p => p());

        var q = Query ?? "";
        var scored = all
            .Select(i =>
            {
                var s = string.IsNullOrWhiteSpace(q) ? i.Score : Fuzzy.Score(q, i.Title);
                return s < 0 ? null : i with { Score = s };
            })
            .Where(x => x is not null)
            .Cast<CommandPaletteItem>()
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title)
            .Take(20)
            .ToList();

        Items.Clear();
        foreach (var item in scored)
            Items.Add(item);

        SelectedItem = Items.FirstOrDefault();
    }

    private IEnumerable<CommandPaletteItem> BuildCoreCommands()
    {
        yield return new CommandPaletteItem(
            "New Thing",
            "Add a new script",
            async _ => await Shell.Current.GoToAsync("thing/new"),
            Score: 70);

        yield return new CommandPaletteItem(
            "Go to Timeline",
            "View run history",
            async _ => await Shell.Current.GoToAsync("//timeline"),
            Score: 50);

        yield return new CommandPaletteItem(
            "Go to Things",
            "View all things",
            async _ => await Shell.Current.GoToAsync("//things"),
            Score: 50);

        yield return new CommandPaletteItem(
            "Go to Failures",
            "View grouped failures",
            async _ => await Shell.Current.GoToAsync("//failures"),
            Score: 50);

        yield return new CommandPaletteItem(
            "Tail latest failure",
            "Open last failed run",
            async _ =>
            {
                var lastFailed = _runs.ListRuns(limit: 200)
                    .FirstOrDefault(r => r.Status == RunStatus.Failed);

                if (lastFailed is not null)
                    await Shell.Current.GoToAsync($"run?runId={lastFailed.RunId}");
                else
                    await Shell.Current.GoToAsync("//timeline");
            },
            Score: 60);

        yield return new CommandPaletteItem(
            "Tail latest run",
            "Open most recent run",
            async _ =>
            {
                var lastRun = _runs.ListRuns(limit: 1).FirstOrDefault();
                if (lastRun is not null)
                    await Shell.Current.GoToAsync($"run?runId={lastRun.RunId}");
                else
                    await Shell.Current.GoToAsync("//timeline");
            },
            Score: 55);
    }

    /// <summary>
    /// Smart commands based on failure patterns
    /// </summary>
    private IEnumerable<CommandPaletteItem> BuildSmartFailureCommands()
    {
        // Most common recurring failure
        var mostCommon = _runs.GetMostCommonFailure();
        if (mostCommon is not null)
        {
            var preview = mostCommon.LastStdErrLine?.Length > 40
                ? mostCommon.LastStdErrLine[..40] + "…"
                : mostCommon.LastStdErrLine ?? "Unknown error";

            yield return new CommandPaletteItem(
                "Tail most common failure",
                $"×{mostCommon.Count} {mostCommon.ThingName}: {preview}",
                async _ => await Shell.Current.GoToAsync($"run?runId={mostCommon.LatestRunId}"),
                Score: 65);
        }

        // Retry last failed run (re-run the same Thing with same profile)
        var lastFailed = _runs.ListRuns(limit: 100)
            .FirstOrDefault(r => r.Status == RunStatus.Failed);

        if (lastFailed is not null)
        {
            var thing = _things.ListThings()
                .FirstOrDefault(t => t.ThingId == lastFailed.ThingId);

            if (thing is not null)
            {
                // Get the profile from the failed run's summary
                var failedSummary = lastFailed.GetParsedSummary();
                var profileId = failedSummary?.ProfileId;
                var profileName = failedSummary?.ProfileName;

                var title = string.IsNullOrEmpty(profileName) || profileName == "Default"
                    ? $"Retry: {thing.Name}"
                    : $"Retry: {thing.Name} ({profileName})";

                yield return new CommandPaletteItem(
                    title,
                    "Re-run last failed script",
                    async ct =>
                    {
                        var domainThing = new Thing(
                            thing.ThingId,
                            thing.Name,
                            thing.Kind,
                            thing.ConfigJson,
                            DateTimeOffset.UtcNow
                        );

                        var runId = await Task.Run(
                            async () => await _runScript.ExecuteWithProfileAsync(
                                domainThing,
                                profileId: profileId,
                                argsOverride: null,
                                ct),
                            ct
                        );

                        await Shell.Current.GoToAsync($"run?runId={runId}");
                    },
                    Score: 62);
            }
        }

        // "Last success for X" commands for Things that have recent failures
        var failedThingIds = _runs.ListRuns(limit: 50)
            .Where(r => r.Status == RunStatus.Failed)
            .Select(r => r.ThingId)
            .Distinct()
            .Take(5);

        foreach (var thingId in failedThingIds)
        {
            var lastSuccess = _runs.GetLastSuccess(thingId);
            if (lastSuccess is not null)
            {
                yield return new CommandPaletteItem(
                    $"Last success: {lastSuccess.ThingName}",
                    $"@ {lastSuccess.StartedAt.LocalDateTime:g}",
                    async _ => await Shell.Current.GoToAsync($"run?runId={lastSuccess.RunId}"),
                    Score: 45);
            }
        }
    }

    private IEnumerable<CommandPaletteItem> BuildThingActions()
    {
        var things = _things.ListThings();

        foreach (var t in things)
        {
            // Capture for closure
            var thing = t;

            // Parse config to get profiles
            var config = ThingConfig.Parse(thing.ConfigJson);
            var profiles = config.Profiles;

            // If no profiles or only default, show simple "Run: X"
            if (profiles.Count <= 1)
            {
                yield return new CommandPaletteItem(
                    $"Run: {thing.Name}",
                    "Execute script",
                    async ct =>
                    {
                        var domainThing = new Thing(
                            thing.ThingId,
                            thing.Name,
                            thing.Kind,
                            thing.ConfigJson,
                            DateTimeOffset.UtcNow
                        );

                        var runId = await Task.Run(
                            async () => await _runScript.ExecuteAsync(domainThing, args: "", ct),
                            ct
                        );

                        await Shell.Current.GoToAsync($"run?runId={runId}");
                    },
                    Score: 80);
            }
            else
            {
                // Multiple profiles: show each as a separate command
                foreach (var profile in profiles)
                {
                    var p = profile; // Capture for closure

                    // First/default profile shows as "Run: X", others show as "Run: X (ProfileName)"
                    var isDefault = profile == profiles[0];
                    var title = isDefault
                        ? $"Run: {thing.Name}"
                        : $"Run: {thing.Name} ({p.Name})";

                    var subtitle = BuildProfileSubtitle(p, isDefault);

                    yield return new CommandPaletteItem(
                        title,
                        subtitle,
                        async ct =>
                        {
                            var domainThing = new Thing(
                                thing.ThingId,
                                thing.Name,
                                thing.Kind,
                                thing.ConfigJson,
                                DateTimeOffset.UtcNow
                            );

                            var runId = await Task.Run(
                                async () => await _runScript.ExecuteWithProfileAsync(
                                    domainThing,
                                    profileId: p.Id,
                                    argsOverride: null,
                                    ct),
                                ct
                            );

                            await Shell.Current.GoToAsync($"run?runId={runId}");
                        },
                        Score: isDefault ? 80 : 78); // Default profile slightly higher
                }
            }
        }
    }

    /// <summary>
    /// Build a descriptive subtitle for a profile command
    /// </summary>
    private static string BuildProfileSubtitle(ThingProfile profile, bool isDefault)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(profile.Args))
        {
            var argsPreview = profile.Args.Length > 30
                ? profile.Args[..30] + "…"
                : profile.Args;
            parts.Add($"args: {argsPreview}");
        }

        if (profile.Env.Count > 0)
        {
            parts.Add($"+{profile.Env.Count} env");
        }

        if (!string.IsNullOrEmpty(profile.WorkingDir))
        {
            parts.Add("custom cwd");
        }

        if (parts.Count == 0)
        {
            return isDefault ? "Execute script (default)" : $"Execute with {profile.Name} profile";
        }

        return string.Join(" · ", parts);
    }

    private IEnumerable<CommandPaletteItem> BuildRecentRunActions()
    {
        var runs = _runs.ListRuns(limit: 20);

        foreach (var r in runs)
        {
            var run = r;
            var summary = run.GetParsedSummary();

            var statusHint = run.Status switch
            {
                RunStatus.Succeeded => "✓",
                RunStatus.Failed => "✗",
                RunStatus.Running => "⋯",
                RunStatus.Canceled => "⊘",
                _ => ""
            };

            // Add duration to subtitle if available
            var subtitle = $"{statusHint} {run.Status}";
            if (summary is not null)
            {
                var duration = summary.Duration.TotalSeconds >= 1
                    ? $"{summary.Duration.TotalSeconds:F1}s"
                    : $"{summary.Duration.TotalMilliseconds:F0}ms";
                subtitle = $"{statusHint} {duration}";

                // Show recurrence count for failures
                if (run.Status == RunStatus.Failed && !string.IsNullOrEmpty(summary.FailureFingerprint))
                {
                    var count = _runs.GetRecurrenceCount(summary.FailureFingerprint);
                    if (count > 1)
                    {
                        subtitle += $" (×{count})";
                    }
                }
            }

            yield return new CommandPaletteItem(
                $"Open: {run.ThingName} @ {run.StartedAt.LocalDateTime:g}",
                subtitle,
                async _ => await Shell.Current.GoToAsync($"run?runId={run.RunId}"),
                Score: 10);
        }
    }
}
