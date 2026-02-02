using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ControlRoom.Application.Services;
using ControlRoom.Application.UseCases;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.App.ViewModels;

public partial class RunbooksViewModel : ObservableObject
{
    private readonly RunbookQueries _runbooks;
    private readonly IRunbookExecutor _executor;
    private readonly IRunbookTemplateService _templates;

    public RunbooksViewModel(RunbookQueries runbooks, IRunbookExecutor executor, IRunbookTemplateService templates)
    {
        _runbooks = runbooks;
        _executor = executor;
        _templates = templates;
        LoadTemplates();
    }

    public ObservableCollection<RunbookListItemViewModel> Runbooks { get; } = [];
    public ObservableCollection<RunbookExecutionListItem> RecentExecutions { get; } = [];
    public ObservableCollection<TemplateCategoryViewModel> TemplateCategories { get; } = [];

    [ObservableProperty]
    private bool isRefreshing;

    [ObservableProperty]
    private bool hasRunbooks;

    [ObservableProperty]
    private RunbookListItemViewModel? selectedRunbook;

    [ObservableProperty]
    private bool isTemplatesVisible;

    [ObservableProperty]
    private RunbookTemplate? selectedTemplate;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        await Task.Run(() =>
        {
            var items = _runbooks.ListRunbooks();
            var executions = _runbooks.ListExecutions(limit: 10);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Runbooks.Clear();
                foreach (var item in items)
                {
                    Runbooks.Add(new RunbookListItemViewModel(item));
                }
                HasRunbooks = Runbooks.Count > 0;

                RecentExecutions.Clear();
                foreach (var exec in executions)
                {
                    RecentExecutions.Add(exec);
                }
            });
        });
        IsRefreshing = false;
    }

    [RelayCommand]
    private async Task NewRunbookAsync()
    {
        await Shell.Current.GoToAsync("runbook/new");
    }

    [RelayCommand]
    private async Task EditRunbookAsync(RunbookListItemViewModel? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"runbook/edit?runbookId={item.RunbookId}");
    }

    [RelayCommand]
    private async Task ExecuteRunbookAsync(RunbookListItemViewModel? item)
    {
        if (item is null || !item.IsEnabled) return;

        var runbook = _runbooks.GetRunbook(item.RunbookId);
        if (runbook is null) return;

        var executionId = await _executor.ExecuteAsync(runbook, "Manual execution from UI");

        // Refresh to show the new execution
        await RefreshAsync();

        // Navigate to execution detail
        await Shell.Current.GoToAsync($"runbook/execution?executionId={executionId}");
    }

    [RelayCommand]
    private async Task ViewExecutionAsync(RunbookExecutionListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"runbook/execution?executionId={item.ExecutionId}");
    }

    [RelayCommand]
    private async Task DeleteRunbookAsync(RunbookListItemViewModel? item)
    {
        if (item is null) return;

        bool confirm = await Shell.Current.DisplayAlert(
            "Delete Runbook",
            $"Are you sure you want to delete '{item.Name}'? This will also delete all execution history.",
            "Delete",
            "Cancel");

        if (confirm)
        {
            await Task.Run(() => _runbooks.DeleteRunbook(item.RunbookId));
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(RunbookListItemViewModel? item)
    {
        if (item is null) return;

        var runbook = _runbooks.GetRunbook(item.RunbookId);
        if (runbook is null) return;

        var updated = runbook with { IsEnabled = !runbook.IsEnabled, UpdatedAt = DateTimeOffset.UtcNow };
        _runbooks.UpdateRunbook(updated);

        item.IsEnabled = updated.IsEnabled;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ShowTemplates()
    {
        IsTemplatesVisible = true;
    }

    [RelayCommand]
    private void HideTemplates()
    {
        IsTemplatesVisible = false;
        SelectedTemplate = null;
    }

    [RelayCommand]
    private async Task CreateFromTemplateAsync(RunbookTemplate? template)
    {
        if (template is null) return;

        string name = await Shell.Current.DisplayPromptAsync(
            "Create from Template",
            $"Enter a name for the new runbook based on '{template.Name}':",
            initialValue: template.Name);

        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var runbook = _templates.CreateFromTemplate(template.Id, name);
            _runbooks.InsertRunbook(runbook);

            IsTemplatesVisible = false;
            await RefreshAsync();

            // Navigate to edit the new runbook
            await Shell.Current.GoToAsync($"runbook/edit?runbookId={runbook.Id}");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to create runbook: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task ExportRunbookAsync(RunbookListItemViewModel? item)
    {
        if (item is null) return;

        try
        {
            var json = _templates.ExportRunbook(item.RunbookId);

            // Copy to clipboard
            await Clipboard.SetTextAsync(json);

            await Shell.Current.DisplayAlert(
                "Exported",
                $"Runbook '{item.Name}' has been exported to clipboard.\n\nYou can paste this JSON to share or backup the runbook.",
                "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to export: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task ImportRunbookAsync()
    {
        string json = await Shell.Current.DisplayPromptAsync(
            "Import Runbook",
            "Paste the exported runbook JSON:",
            maxLength: 100000);

        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var result = _templates.ImportRunbook(json);

            if (result.Success)
            {
                await Shell.Current.DisplayAlert(
                    "Imported",
                    result.WasOverwritten
                        ? "Runbook was updated successfully."
                        : "Runbook was imported successfully.",
                    "OK");

                await RefreshAsync();

                if (result.RunbookId.HasValue)
                {
                    await Shell.Current.GoToAsync($"runbook/edit?runbookId={result.RunbookId}");
                }
            }
            else
            {
                await Shell.Current.DisplayAlert("Import Failed", result.ErrorMessage ?? "Unknown error", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to import: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task SaveAsTemplateAsync(RunbookListItemViewModel? item)
    {
        if (item is null) return;

        string category = await Shell.Current.DisplayPromptAsync(
            "Save as Template",
            "Enter a category for this template (e.g., CI/CD, Backup, Monitoring):",
            initialValue: "Custom");

        if (string.IsNullOrWhiteSpace(category)) return;

        string description = await Shell.Current.DisplayPromptAsync(
            "Template Description",
            "Enter a description for this template:",
            initialValue: item.Description);

        if (description is null) return;

        try
        {
            _templates.SaveAsTemplate(item.RunbookId, category, description, Array.Empty<string>());

            await Shell.Current.DisplayAlert(
                "Saved",
                $"'{item.Name}' has been saved as a template in the '{category}' category.",
                "OK");

            LoadTemplates();
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to save template: {ex.Message}", "OK");
        }
    }

    private void LoadTemplates()
    {
        var templates = _templates.GetTemplates();
        var categories = templates
            .GroupBy(t => t.Category)
            .Select(g => new TemplateCategoryViewModel(g.Key, g.ToList()))
            .OrderBy(c => c.Name)
            .ToList();

        TemplateCategories.Clear();
        foreach (var cat in categories)
        {
            TemplateCategories.Add(cat);
        }
    }
}

/// <summary>
/// View model wrapper for RunbookListItem
/// </summary>
public partial class RunbookListItemViewModel : ObservableObject
{
    private readonly RunbookListItem _item;

    public RunbookListItemViewModel(RunbookListItem item)
    {
        _item = item;
        isEnabled = item.IsEnabled;
    }

    public RunbookId RunbookId => _item.RunbookId;
    public string Name => _item.Name;
    public string Description => _item.Description;
    public int StepCount => _item.StepCount;
    public int Version => _item.Version;
    public DateTimeOffset CreatedAt => _item.CreatedAt;
    public DateTimeOffset UpdatedAt => _item.UpdatedAt;

    public string TriggerDisplay => _item.TriggerType switch
    {
        TriggerType.Manual => "Manual",
        TriggerType.Schedule => "Scheduled",
        TriggerType.Webhook => "Webhook",
        TriggerType.FileWatch => "File Watch",
        _ => "Manual"
    };

    public string StepCountText => _item.StepCount == 1 ? "1 step" : $"{_item.StepCount} steps";
    public string LastUpdatedText => GetRelativeTime(_item.UpdatedAt);

    [ObservableProperty]
    private bool isEnabled;

    public string StatusColor => IsEnabled ? "#2196F3" : "#9E9E9E";

    private static string GetRelativeTime(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return time.ToString("MMM d");
    }
}

/// <summary>
/// View model for template categories
/// </summary>
public sealed class TemplateCategoryViewModel
{
    public string Name { get; }
    public IReadOnlyList<RunbookTemplate> Templates { get; }
    public int Count => Templates.Count;

    public TemplateCategoryViewModel(string name, IReadOnlyList<RunbookTemplate> templates)
    {
        Name = name;
        Templates = templates;
    }
}
