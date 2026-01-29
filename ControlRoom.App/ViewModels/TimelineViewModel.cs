using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

public partial class TimelineViewModel : ObservableObject, IQueryAttributable
{
    private readonly RunQueries _runs;
    private string? _filterFingerprint;

    public TimelineViewModel(RunQueries runs)
    {
        _runs = runs;
    }

    public ObservableCollection<TimelineRunItem> Runs { get; } = [];

    [ObservableProperty]
    private bool isRefreshing;

    [ObservableProperty]
    private bool isFiltered;

    [ObservableProperty]
    private string filterChipText = "";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("fingerprint", out var fpObj) && fpObj is string fp && !string.IsNullOrEmpty(fp))
        {
            _filterFingerprint = fp;
            IsFiltered = true;

            // Get the failure group to show count in chip
            var count = _runs.GetRecurrenceCount(fp);
            var shortFp = fp.Length > 8 ? fp[..8] : fp;
            FilterChipText = $"Failure ×{count} ({shortFp})";
        }
        else
        {
            ClearFilter();
        }
    }

    [RelayCommand]
    private void ClearFilter()
    {
        _filterFingerprint = null;
        IsFiltered = false;
        FilterChipText = "";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        await Task.Run(() =>
        {
            IReadOnlyList<RunListItem> items;

            if (!string.IsNullOrEmpty(_filterFingerprint))
            {
                // Filtered mode: show only runs with this fingerprint
                items = _runs.ListRunsByFingerprint(_filterFingerprint, limit: 100);
            }
            else
            {
                // Normal mode: show all runs
                items = _runs.ListRuns(limit: 300);
            }

            // Build recurrence counts for failed runs (batch lookup)
            var recurrenceCounts = new Dictionary<string, int>();
            foreach (var item in items)
            {
                if (item.Status == RunStatus.Failed)
                {
                    var summary = item.GetParsedSummary();
                    var fp = summary?.FailureFingerprint;
                    if (!string.IsNullOrEmpty(fp) && !recurrenceCounts.ContainsKey(fp))
                    {
                        recurrenceCounts[fp] = _runs.GetRecurrenceCount(fp);
                    }
                }
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Runs.Clear();
                foreach (var item in items)
                {
                    var fp = item.GetParsedSummary()?.FailureFingerprint;
                    var count = !string.IsNullOrEmpty(fp) && recurrenceCounts.TryGetValue(fp, out var c) ? c : 0;
                    Runs.Add(new TimelineRunItem(item, count));
                }
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
    private async Task OpenRunAsync(TimelineRunItem? run)
    {
        if (run is null) return;
        await Shell.Current.GoToAsync($"run?runId={run.RunId}");
    }
}
