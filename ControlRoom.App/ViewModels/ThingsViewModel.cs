using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;
using ControlRoom.Domain.Services;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

public partial class ThingsViewModel : ObservableObject
{
    private readonly ThingQueries _things;
    private readonly RunLocalScript _runScript;
    private readonly IAIAssistant? _aiAssistant;
    private readonly RunQueries _runs;

    public ThingsViewModel(ThingQueries things, RunLocalScript runScript, RunQueries runs, IAIAssistant? aiAssistant = null)
    {
        _things = things;
        _runScript = runScript;
        _runs = runs;
        _aiAssistant = aiAssistant;
    }

    public ObservableCollection<ThingRunItem> Things { get; } = [];

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
                {
                    Things.Add(new ThingRunItem(item, _aiAssistant, _runs));
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
    private async Task RunThingAsync(ThingRunItem? item)
    {
        if (item is null) return;

        // Convert to Thing domain object
        var thing = new Thing(
            item.Item.ThingId,
            item.Item.Name,
            item.Item.Kind,
            item.Item.ConfigJson,
            DateTimeOffset.UtcNow // not used for run
        );

        // Run with the provided arguments
        var runId = await Task.Run(async () =>
            await _runScript.ExecuteAsync(thing, args: item.Arguments, CancellationToken.None)
        );

        // Store the args in history for this thing
        item.AddToHistory(item.Arguments);

        await Shell.Current.GoToAsync($"run?runId={runId}");
    }

    [RelayCommand]
    private void ToggleExpand(ThingRunItem? item)
    {
        if (item is null) return;
        item.IsExpanded = !item.IsExpanded;

        // Collapse others
        foreach (var other in Things.Where(t => t != item))
        {
            other.IsExpanded = false;
        }

        // Load AI suggestions when expanding
        if (item.IsExpanded)
        {
            _ = item.LoadSuggestionsAsync();
        }
    }
}

/// <summary>
/// Wrapper for ThingListItem that adds run-specific state (arguments, suggestions)
/// </summary>
public partial class ThingRunItem : ObservableObject
{
    private readonly IAIAssistant? _aiAssistant;
    private readonly RunQueries _runs;
    private readonly List<string> _argsHistory = new();

    public ThingRunItem(ThingListItem item, IAIAssistant? aiAssistant, RunQueries runs)
    {
        Item = item;
        _aiAssistant = aiAssistant;
        _runs = runs;

        // Load argument history from recent runs
        LoadArgsHistory();
    }

    public ThingListItem Item { get; }

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private string arguments = "";

    [ObservableProperty]
    private bool isLoadingSuggestions;

    [ObservableProperty]
    private string currentArgDescription = "";

    public ObservableCollection<ArgSuggestionItem> Suggestions { get; } = new();

    private void LoadArgsHistory()
    {
        // Get recent runs for this thing to extract argument history
        var runs = _runs.ListRuns(limit: 20);
        foreach (var run in runs.Where(r => r.ThingId == Item.ThingId))
        {
            var summary = run.GetParsedSummary();
            var cmdLine = summary?.CommandLine;
            if (!string.IsNullOrEmpty(cmdLine))
            {
                // Extract args portion (after the script path)
                var parts = cmdLine.Split(' ', 2);
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    var args = parts[1].Trim();
                    if (!_argsHistory.Contains(args))
                    {
                        _argsHistory.Add(args);
                    }
                }
            }
        }
    }

    public void AddToHistory(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return;
        _argsHistory.Remove(args);
        _argsHistory.Insert(0, args);
        if (_argsHistory.Count > 10)
        {
            _argsHistory.RemoveAt(_argsHistory.Count - 1);
        }
    }

    public async Task LoadSuggestionsAsync()
    {
        if (_aiAssistant is null)
        {
            // Fall back to history-only suggestions
            LoadHistorySuggestions();
            return;
        }

        IsLoadingSuggestions = true;
        Suggestions.Clear();

        try
        {
            // Get script path from config
            var config = ThingConfig.Parse(Item.ConfigJson);
            var scriptPath = config.Path;

            var result = await _aiAssistant.AutocompleteArgsAsync(
                scriptPath,
                Arguments,
                _argsHistory
            );

            CurrentArgDescription = result.CurrentArgDescription ?? "";

            foreach (var suggestion in result.Suggestions.Take(8))
            {
                Suggestions.Add(new ArgSuggestionItem
                {
                    Value = suggestion.Value,
                    Description = suggestion.Description,
                    Source = suggestion.Source.ToString(),
                    IsFromHistory = suggestion.Source == ArgumentSource.History
                });
            }
        }
        catch
        {
            LoadHistorySuggestions();
        }
        finally
        {
            IsLoadingSuggestions = false;
        }
    }

    private void LoadHistorySuggestions()
    {
        Suggestions.Clear();
        foreach (var hist in _argsHistory.Take(5))
        {
            Suggestions.Add(new ArgSuggestionItem
            {
                Value = hist,
                Description = "Previously used",
                Source = "History",
                IsFromHistory = true
            });
        }
    }

    [RelayCommand]
    private void UseSuggestion(ArgSuggestionItem? suggestion)
    {
        if (suggestion is null) return;
        Arguments = suggestion.Value;
    }
}

/// <summary>
/// Argument suggestion for display
/// </summary>
public class ArgSuggestionItem
{
    public string Value { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsFromHistory { get; init; }
}
