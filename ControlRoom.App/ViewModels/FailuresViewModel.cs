using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

public partial class FailuresViewModel : ObservableObject
{
    private readonly RunQueries _runs;
    private readonly ThingQueries _things;
    private readonly RunLocalScript _runScript;

    public FailuresViewModel(RunQueries runs, ThingQueries things, RunLocalScript runScript)
    {
        _runs = runs;
        _things = things;
        _runScript = runScript;
    }

    public ObservableCollection<FailureGroupItem> FailureGroups { get; } = [];

    [ObservableProperty]
    private bool isRefreshing;

    [ObservableProperty]
    private bool hasFailures;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        await Task.Run(() =>
        {
            var groups = _runs.GetAllFailureGroups(limit: 100);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                FailureGroups.Clear();
                foreach (var group in groups)
                {
                    FailureGroups.Add(new FailureGroupItem(group));
                }
                HasFailures = FailureGroups.Count > 0;
            });
        });
        IsRefreshing = false;
    }

    /// <summary>
    /// Open the most recent run for a failure group
    /// </summary>
    [RelayCommand]
    private async Task OpenLatestAsync(FailureGroupItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"run?runId={item.LatestRunId}");
    }

    /// <summary>
    /// Open the first (oldest) run for a failure group
    /// </summary>
    [RelayCommand]
    private async Task OpenFirstAsync(FailureGroupItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"run?runId={item.FirstRunId}");
    }

    /// <summary>
    /// Navigate to Timeline filtered by this failure fingerprint
    /// </summary>
    [RelayCommand]
    private async Task ShowAllRunsAsync(FailureGroupItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"//timeline?fingerprint={item.Fingerprint}");
    }

    /// <summary>
    /// Retry the latest Thing that had this failure
    /// </summary>
    [RelayCommand]
    private async Task RetryLatestAsync(FailureGroupItem? item, CancellationToken ct = default)
    {
        if (item is null) return;

        // Get the ThingId for the latest failure with this fingerprint
        var thingId = _runs.GetLatestThingForFingerprint(item.Fingerprint);
        if (thingId is null) return;

        // Get the Thing details
        var thingItem = _things.GetThing(thingId.Value);
        if (thingItem is null) return;

        // Create domain Thing and execute
        var domainThing = new Thing(
            thingItem.ThingId,
            thingItem.Name,
            thingItem.Kind,
            thingItem.ConfigJson,
            DateTimeOffset.UtcNow
        );

        var runId = await Task.Run(
            async () => await _runScript.ExecuteAsync(domainThing, args: "", ct),
            ct
        );

        // Navigate to the new run
        await Shell.Current.GoToAsync($"run?runId={runId}");
    }
}
