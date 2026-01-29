using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

public partial class ThingsViewModel : ObservableObject
{
    private readonly ThingQueries _things;
    private readonly RunLocalScript _runScript;

    public ThingsViewModel(ThingQueries things, RunLocalScript runScript)
    {
        _things = things;
        _runScript = runScript;
    }

    public ObservableCollection<ThingListItem> Things { get; } = [];

    [ObservableProperty]
    private bool isRefreshing;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        await Task.Run(() =>
        {
            var items = _things.ListThings();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Things.Clear();
                foreach (var item in items)
                    Things.Add(item);
            });
        });
        IsRefreshing = false;
    }

    [RelayCommand]
    private async Task NewThingAsync()
    {
        await Shell.Current.GoToAsync("thing/new");
    }

    [RelayCommand]
    private async Task RunThingAsync(ThingListItem? item)
    {
        if (item is null) return;

        // Convert to Thing domain object
        var thing = new Thing(
            item.ThingId,
            item.Name,
            item.Kind,
            item.ConfigJson,
            DateTimeOffset.UtcNow // not used for run
        );

        // Run in background, navigate to run detail
        var runId = await Task.Run(async () =>
            await _runScript.ExecuteAsync(thing, args: "", CancellationToken.None)
        );

        await Shell.Current.GoToAsync($"run?runId={runId}");
    }
}
