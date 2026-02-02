using System.Text.Json;
using System.Text.Json.Serialization;
using ControlRoom.Domain.Model;
using ControlRoom.Infrastructure.Storage.Queries;

namespace ControlRoom.Application.Services;

/// <summary>
/// Service for managing runbook templates and import/export
/// </summary>
public interface IRunbookTemplateService
{
    /// <summary>
    /// Get all available templates (built-in and custom)
    /// </summary>
    IReadOnlyList<RunbookTemplate> GetTemplates();

    /// <summary>
    /// Get templates by category
    /// </summary>
    IReadOnlyList<RunbookTemplate> GetTemplatesByCategory(string category);

    /// <summary>
    /// Create a runbook from a template
    /// </summary>
    Runbook CreateFromTemplate(string templateId, string newName);

    /// <summary>
    /// Export a runbook to shareable JSON
    /// </summary>
    string ExportRunbook(RunbookId runbookId);

    /// <summary>
    /// Import a runbook from JSON
    /// </summary>
    RunbookImportResult ImportRunbook(string json, bool overwriteExisting = false);

    /// <summary>
    /// Save current runbook as a custom template
    /// </summary>
    void SaveAsTemplate(RunbookId runbookId, string category, string description, string[] tags);

    /// <summary>
    /// Delete a custom template
    /// </summary>
    void DeleteTemplate(string templateId);
}

/// <summary>
/// A runbook template
/// </summary>
public sealed record RunbookTemplate(
    string Id,
    string Name,
    string Description,
    string Category,
    string[] Tags,
    bool IsBuiltIn,
    RunbookTemplateContent Content
);

/// <summary>
/// Content of a runbook template
/// </summary>
public sealed class RunbookTemplateContent
{
    public List<TemplateStepDef> Steps { get; set; } = [];
    public TemplateTriggerDef? Trigger { get; set; }
    public Dictionary<string, string> Variables { get; set; } = [];
}

/// <summary>
/// Step definition in a template
/// </summary>
public sealed class TemplateStepDef
{
    public string StepId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ScriptType { get; set; } = ""; // e.g., "powershell", "python"
    public string ScriptTemplate { get; set; } = ""; // Template with {{variables}}
    public string Condition { get; set; } = "OnSuccess";
    public string[] DependsOn { get; set; } = [];
    public bool EnableRetry { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; }
}

/// <summary>
/// Trigger definition in a template
/// </summary>
public sealed class TemplateTriggerDef
{
    public string Type { get; set; } = "Manual"; // Manual, Schedule, Webhook, FileWatch
    public string? CronExpression { get; set; }
    public string? WatchPath { get; set; }
    public string? WatchPattern { get; set; }
}

/// <summary>
/// Result of importing a runbook
/// </summary>
public sealed record RunbookImportResult(
    bool Success,
    RunbookId? RunbookId,
    string? ErrorMessage,
    bool WasOverwritten
);

/// <summary>
/// Runbook export format
/// </summary>
public sealed class RunbookExportFormat
{
    public const string FormatVersion = "1.0";

    public string Version { get; set; } = FormatVersion;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; }
    public string ExportedFrom { get; set; } = "Control Room";
    public RunbookConfig Config { get; set; } = new();

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, ExportJsonOptions);
    }

    public static RunbookExportFormat? FromJson(string json)
    {
        return JsonSerializer.Deserialize<RunbookExportFormat>(json, ExportJsonOptions);
    }

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Implementation of RunbookTemplateService
/// </summary>
public sealed class RunbookTemplateService : IRunbookTemplateService
{
    private readonly RunbookQueries _runbooks;
    private readonly ThingQueries _things;
    private readonly List<RunbookTemplate> _builtInTemplates;
    private readonly Dictionary<string, RunbookTemplate> _customTemplates = new();
    private readonly string _customTemplatesPath;

    public RunbookTemplateService(RunbookQueries runbooks, ThingQueries things)
    {
        _runbooks = runbooks;
        _things = things;
        _builtInTemplates = CreateBuiltInTemplates();

        // Load custom templates from disk
        _customTemplatesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlRoom",
            "templates");

        LoadCustomTemplates();
    }

    public IReadOnlyList<RunbookTemplate> GetTemplates()
    {
        return _builtInTemplates.Concat(_customTemplates.Values).ToList();
    }

    public IReadOnlyList<RunbookTemplate> GetTemplatesByCategory(string category)
    {
        return GetTemplates()
            .Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public Runbook CreateFromTemplate(string templateId, string newName)
    {
        var template = GetTemplates().FirstOrDefault(t => t.Id == templateId);
        if (template is null)
        {
            throw new InvalidOperationException($"Template not found: {templateId}");
        }

        // For built-in templates, we need to create Things from the template scripts
        // For now, we'll create a basic runbook structure that can be customized
        var steps = template.Content.Steps.Select((s, i) => new RunbookStep(
            s.StepId,
            s.Name,
            ThingId.New(), // Placeholder - user needs to assign scripts
            "default",
            s.Condition switch
            {
                "Always" => StepCondition.Always,
                "OnFailure" => StepCondition.OnFailure,
                _ => StepCondition.OnSuccess
            },
            s.DependsOn.ToList(),
            s.EnableRetry ? new RetryPolicy(s.MaxRetries, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5)) : null,
            s.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(s.TimeoutSeconds) : null,
            null
        )).ToList();

        RunbookTrigger? trigger = template.Content.Trigger?.Type switch
        {
            "Schedule" when template.Content.Trigger.CronExpression is not null =>
                new ScheduleTrigger(template.Content.Trigger.CronExpression),
            "Webhook" => new WebhookTrigger(Guid.NewGuid().ToString("N")),
            "FileWatch" when template.Content.Trigger.WatchPath is not null =>
                new FileWatchTrigger(
                    template.Content.Trigger.WatchPath,
                    template.Content.Trigger.WatchPattern ?? "*.*"),
            _ => new ManualTrigger()
        };

        var now = DateTimeOffset.UtcNow;
        return new Runbook(
            RunbookId.New(),
            newName,
            template.Description,
            steps,
            trigger,
            true,
            now,
            now
        );
    }

    public string ExportRunbook(RunbookId runbookId)
    {
        var runbook = _runbooks.GetRunbook(runbookId);
        if (runbook is null)
        {
            throw new InvalidOperationException($"Runbook not found: {runbookId}");
        }

        var config = new RunbookConfig
        {
            Name = runbook.Name,
            Description = runbook.Description,
            Steps = runbook.Steps.Select(s => new RunbookStepConfig
            {
                StepId = s.StepId,
                Name = s.Name,
                ThingId = s.ThingId.ToString(),
                ProfileId = s.ProfileId,
                ConditionType = s.Condition.Type,
                ConditionExpression = s.Condition.Expression,
                DependsOn = s.DependsOn.ToList(),
                Retry = s.Retry is null ? null : new RetryPolicyConfig
                {
                    MaxAttempts = s.Retry.MaxAttempts,
                    InitialDelaySeconds = (int)s.Retry.InitialDelay.TotalSeconds,
                    BackoffMultiplier = s.Retry.BackoffMultiplier,
                    MaxDelaySeconds = (int)s.Retry.MaxDelay.TotalSeconds
                },
                TimeoutSeconds = s.Timeout.HasValue ? (int)s.Timeout.Value.TotalSeconds : null,
                ArgumentsOverride = s.ArgumentsOverride
            }).ToList(),
            Trigger = runbook.Trigger,
            IsEnabled = runbook.IsEnabled
        };

        var export = new RunbookExportFormat
        {
            Name = runbook.Name,
            Description = runbook.Description,
            ExportedAt = DateTimeOffset.UtcNow,
            Config = config
        };

        return export.ToJson();
    }

    public RunbookImportResult ImportRunbook(string json, bool overwriteExisting = false)
    {
        try
        {
            var export = RunbookExportFormat.FromJson(json);
            if (export is null)
            {
                return new RunbookImportResult(false, null, "Invalid runbook format", false);
            }

            // Check if runbook with same name exists
            var existing = _runbooks.GetRunbookByName(export.Name);
            if (existing is not null && !overwriteExisting)
            {
                return new RunbookImportResult(false, null,
                    $"Runbook '{export.Name}' already exists. Use overwrite option to replace.", false);
            }

            // Build the runbook from config
            var steps = export.Config.Steps.Select(s => s.ToStep()).ToList();
            var now = DateTimeOffset.UtcNow;

            var runbook = new Runbook(
                existing?.Id ?? RunbookId.New(),
                export.Config.Name,
                export.Config.Description,
                steps,
                export.Config.Trigger,
                export.Config.IsEnabled,
                existing?.CreatedAt ?? now,
                now
            );

            // Validate
            var validation = runbook.Validate();
            if (!validation.IsValid)
            {
                return new RunbookImportResult(false, null,
                    $"Validation failed: {string.Join(", ", validation.Errors)}", false);
            }

            // Save
            if (existing is not null)
            {
                _runbooks.UpdateRunbook(runbook);
                return new RunbookImportResult(true, runbook.Id, null, true);
            }
            else
            {
                _runbooks.InsertRunbook(runbook);
                return new RunbookImportResult(true, runbook.Id, null, false);
            }
        }
        catch (JsonException ex)
        {
            return new RunbookImportResult(false, null, $"Invalid JSON: {ex.Message}", false);
        }
        catch (Exception ex)
        {
            return new RunbookImportResult(false, null, $"Import failed: {ex.Message}", false);
        }
    }

    public void SaveAsTemplate(RunbookId runbookId, string category, string description, string[] tags)
    {
        var runbook = _runbooks.GetRunbook(runbookId);
        if (runbook is null)
        {
            throw new InvalidOperationException($"Runbook not found: {runbookId}");
        }

        var templateId = $"custom-{Guid.NewGuid():N}";

        var content = new RunbookTemplateContent
        {
            Steps = runbook.Steps.Select(s => new TemplateStepDef
            {
                StepId = s.StepId,
                Name = s.Name,
                Condition = s.Condition.Type.ToString(),
                DependsOn = s.DependsOn.ToArray(),
                EnableRetry = s.Retry is not null,
                MaxRetries = s.Retry?.MaxAttempts ?? 3,
                TimeoutSeconds = s.Timeout.HasValue ? (int)s.Timeout.Value.TotalSeconds : 0
            }).ToList(),
            Trigger = runbook.Trigger switch
            {
                ScheduleTrigger schedule => new TemplateTriggerDef
                {
                    Type = "Schedule",
                    CronExpression = schedule.CronExpression
                },
                FileWatchTrigger fileWatch => new TemplateTriggerDef
                {
                    Type = "FileWatch",
                    WatchPath = fileWatch.Path,
                    WatchPattern = fileWatch.Pattern
                },
                WebhookTrigger => new TemplateTriggerDef { Type = "Webhook" },
                _ => new TemplateTriggerDef { Type = "Manual" }
            }
        };

        var template = new RunbookTemplate(
            templateId,
            runbook.Name,
            description,
            category,
            tags,
            false,
            content
        );

        _customTemplates[templateId] = template;
        SaveCustomTemplates();
    }

    public void DeleteTemplate(string templateId)
    {
        if (_builtInTemplates.Any(t => t.Id == templateId))
        {
            throw new InvalidOperationException("Cannot delete built-in templates");
        }

        if (_customTemplates.Remove(templateId))
        {
            SaveCustomTemplates();
        }
    }

    private void LoadCustomTemplates()
    {
        if (!Directory.Exists(_customTemplatesPath)) return;

        foreach (var file in Directory.GetFiles(_customTemplatesPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var template = JsonSerializer.Deserialize<RunbookTemplate>(json);
                if (template is not null)
                {
                    _customTemplates[template.Id] = template;
                }
            }
            catch
            {
                // Skip invalid template files
            }
        }
    }

    private void SaveCustomTemplates()
    {
        Directory.CreateDirectory(_customTemplatesPath);

        foreach (var template in _customTemplates.Values)
        {
            var path = Path.Combine(_customTemplatesPath, $"{template.Id}.json");
            var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    private static List<RunbookTemplate> CreateBuiltInTemplates()
    {
        return new List<RunbookTemplate>
        {
            // CI/CD Templates
            new(
                "builtin-cicd-basic",
                "Basic CI/CD Pipeline",
                "A simple build → test → deploy pipeline with conditional rollback",
                "CI/CD",
                new[] { "build", "test", "deploy", "beginner" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "build", Name = "Build Application", ScriptType = "powershell", Condition = "Always" },
                        new() { StepId = "test", Name = "Run Tests", ScriptType = "powershell", DependsOn = new[] { "build" } },
                        new() { StepId = "deploy", Name = "Deploy to Staging", ScriptType = "powershell", DependsOn = new[] { "test" } },
                        new() { StepId = "rollback", Name = "Rollback on Failure", ScriptType = "powershell", Condition = "OnFailure", DependsOn = new[] { "deploy" } }
                    }
                }
            ),

            new(
                "builtin-cicd-multi-env",
                "Multi-Environment Deployment",
                "Deploy to dev, staging, and production with approvals",
                "CI/CD",
                new[] { "deploy", "multi-environment", "staging", "production" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "build", Name = "Build", ScriptType = "powershell" },
                        new() { StepId = "test", Name = "Run Tests", ScriptType = "powershell", DependsOn = new[] { "build" } },
                        new() { StepId = "deploy-dev", Name = "Deploy to Dev", ScriptType = "powershell", DependsOn = new[] { "test" } },
                        new() { StepId = "verify-dev", Name = "Verify Dev Deployment", ScriptType = "powershell", DependsOn = new[] { "deploy-dev" } },
                        new() { StepId = "deploy-staging", Name = "Deploy to Staging", ScriptType = "powershell", DependsOn = new[] { "verify-dev" } },
                        new() { StepId = "smoke-test", Name = "Staging Smoke Tests", ScriptType = "powershell", DependsOn = new[] { "deploy-staging" } },
                        new() { StepId = "deploy-prod", Name = "Deploy to Production", ScriptType = "powershell", DependsOn = new[] { "smoke-test" } },
                        new() { StepId = "notify", Name = "Send Notification", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "deploy-prod" } }
                    }
                }
            ),

            // Backup Templates
            new(
                "builtin-backup-database",
                "Database Backup Pipeline",
                "Automated database backup with compression, verification, and cleanup",
                "Backup",
                new[] { "database", "backup", "scheduled" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "pre-check", Name = "Pre-Backup Health Check", ScriptType = "powershell" },
                        new() { StepId = "backup", Name = "Create Database Backup", ScriptType = "powershell", DependsOn = new[] { "pre-check" }, EnableRetry = true, MaxRetries = 3 },
                        new() { StepId = "compress", Name = "Compress Backup", ScriptType = "powershell", DependsOn = new[] { "backup" } },
                        new() { StepId = "verify", Name = "Verify Backup Integrity", ScriptType = "powershell", DependsOn = new[] { "compress" } },
                        new() { StepId = "upload", Name = "Upload to Cloud Storage", ScriptType = "powershell", DependsOn = new[] { "verify" }, EnableRetry = true },
                        new() { StepId = "cleanup", Name = "Cleanup Old Backups", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "upload" } },
                        new() { StepId = "report", Name = "Send Backup Report", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "cleanup" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "0 2 * * *" }
                }
            ),

            new(
                "builtin-backup-files",
                "File System Backup",
                "Incremental file backup with deduplication",
                "Backup",
                new[] { "files", "backup", "incremental" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "scan", Name = "Scan for Changes", ScriptType = "powershell" },
                        new() { StepId = "backup", Name = "Backup Changed Files", ScriptType = "powershell", DependsOn = new[] { "scan" } },
                        new() { StepId = "dedupe", Name = "Deduplicate", ScriptType = "powershell", DependsOn = new[] { "backup" } },
                        new() { StepId = "verify", Name = "Verify Backup", ScriptType = "powershell", DependsOn = new[] { "dedupe" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "0 3 * * *" }
                }
            ),

            // Monitoring Templates
            new(
                "builtin-monitor-health",
                "System Health Check",
                "Comprehensive system health monitoring with alerting",
                "Monitoring",
                new[] { "health", "monitoring", "alerts" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "cpu", Name = "Check CPU Usage", ScriptType = "powershell" },
                        new() { StepId = "memory", Name = "Check Memory Usage", ScriptType = "powershell" },
                        new() { StepId = "disk", Name = "Check Disk Space", ScriptType = "powershell" },
                        new() { StepId = "services", Name = "Check Critical Services", ScriptType = "powershell" },
                        new() { StepId = "network", Name = "Check Network Connectivity", ScriptType = "powershell" },
                        new() { StepId = "report", Name = "Generate Health Report", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "cpu", "memory", "disk", "services", "network" } },
                        new() { StepId = "alert", Name = "Send Alerts if Issues", ScriptType = "powershell", Condition = "OnFailure", DependsOn = new[] { "cpu", "memory", "disk", "services", "network" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "*/15 * * * *" }
                }
            ),

            new(
                "builtin-monitor-endpoints",
                "API Endpoint Monitoring",
                "Monitor HTTP endpoints with latency tracking",
                "Monitoring",
                new[] { "api", "http", "latency", "uptime" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "endpoints", Name = "Check API Endpoints", ScriptType = "powershell", TimeoutSeconds = 30 },
                        new() { StepId = "latency", Name = "Measure Response Times", ScriptType = "powershell", DependsOn = new[] { "endpoints" } },
                        new() { StepId = "report", Name = "Log Metrics", ScriptType = "powershell", DependsOn = new[] { "latency" } },
                        new() { StepId = "alert", Name = "Alert on Degradation", ScriptType = "powershell", Condition = "OnFailure", DependsOn = new[] { "endpoints" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "*/5 * * * *" }
                }
            ),

            // Maintenance Templates
            new(
                "builtin-maintenance-cleanup",
                "System Cleanup",
                "Clean temporary files, logs, and caches",
                "Maintenance",
                new[] { "cleanup", "temp", "logs", "cache" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "temp-files", Name = "Clean Temp Files", ScriptType = "powershell" },
                        new() { StepId = "logs", Name = "Archive Old Logs", ScriptType = "powershell" },
                        new() { StepId = "cache", Name = "Clear Cache", ScriptType = "powershell" },
                        new() { StepId = "recycle-bin", Name = "Empty Recycle Bin", ScriptType = "powershell" },
                        new() { StepId = "report", Name = "Report Space Recovered", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "temp-files", "logs", "cache", "recycle-bin" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "0 4 * * 0" }
                }
            ),

            new(
                "builtin-maintenance-updates",
                "Windows Updates Workflow",
                "Managed Windows updates with reboot handling",
                "Maintenance",
                new[] { "windows", "updates", "patching" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "check", Name = "Check for Updates", ScriptType = "powershell" },
                        new() { StepId = "download", Name = "Download Updates", ScriptType = "powershell", DependsOn = new[] { "check" } },
                        new() { StepId = "backup", Name = "Create System Restore Point", ScriptType = "powershell", DependsOn = new[] { "download" } },
                        new() { StepId = "install", Name = "Install Updates", ScriptType = "powershell", DependsOn = new[] { "backup" }, EnableRetry = true },
                        new() { StepId = "verify", Name = "Verify Installation", ScriptType = "powershell", DependsOn = new[] { "install" } },
                        new() { StepId = "notify", Name = "Notify Completion", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "verify" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "0 3 * * 3" }
                }
            ),

            // Data Processing Templates
            new(
                "builtin-data-etl",
                "ETL Pipeline",
                "Extract, Transform, Load data processing",
                "Data Processing",
                new[] { "etl", "data", "pipeline" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "extract", Name = "Extract Data", ScriptType = "python", EnableRetry = true },
                        new() { StepId = "validate", Name = "Validate Source Data", ScriptType = "python", DependsOn = new[] { "extract" } },
                        new() { StepId = "transform", Name = "Transform Data", ScriptType = "python", DependsOn = new[] { "validate" } },
                        new() { StepId = "load", Name = "Load to Target", ScriptType = "python", DependsOn = new[] { "transform" }, EnableRetry = true },
                        new() { StepId = "verify", Name = "Verify Load", ScriptType = "python", DependsOn = new[] { "load" } },
                        new() { StepId = "report", Name = "Generate Report", ScriptType = "python", Condition = "Always", DependsOn = new[] { "verify" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "0 5 * * *" }
                }
            ),

            new(
                "builtin-data-sync",
                "Data Synchronization",
                "Two-way data sync between systems",
                "Data Processing",
                new[] { "sync", "data", "bidirectional" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "lock", Name = "Acquire Sync Lock", ScriptType = "powershell" },
                        new() { StepId = "fetch-a", Name = "Fetch Changes from System A", ScriptType = "powershell", DependsOn = new[] { "lock" } },
                        new() { StepId = "fetch-b", Name = "Fetch Changes from System B", ScriptType = "powershell", DependsOn = new[] { "lock" } },
                        new() { StepId = "resolve", Name = "Resolve Conflicts", ScriptType = "powershell", DependsOn = new[] { "fetch-a", "fetch-b" } },
                        new() { StepId = "apply-a", Name = "Apply to System A", ScriptType = "powershell", DependsOn = new[] { "resolve" } },
                        new() { StepId = "apply-b", Name = "Apply to System B", ScriptType = "powershell", DependsOn = new[] { "resolve" } },
                        new() { StepId = "verify", Name = "Verify Sync", ScriptType = "powershell", DependsOn = new[] { "apply-a", "apply-b" } },
                        new() { StepId = "unlock", Name = "Release Sync Lock", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "verify" } }
                    }
                }
            ),

            // Security Templates
            new(
                "builtin-security-audit",
                "Security Audit",
                "Comprehensive security compliance check",
                "Security",
                new[] { "security", "audit", "compliance" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "accounts", Name = "Audit User Accounts", ScriptType = "powershell" },
                        new() { StepId = "permissions", Name = "Check File Permissions", ScriptType = "powershell" },
                        new() { StepId = "ports", Name = "Scan Open Ports", ScriptType = "powershell" },
                        new() { StepId = "updates", Name = "Check Security Updates", ScriptType = "powershell" },
                        new() { StepId = "logs", Name = "Review Security Logs", ScriptType = "powershell" },
                        new() { StepId = "report", Name = "Generate Audit Report", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "accounts", "permissions", "ports", "updates", "logs" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "0 6 1 * *" }
                }
            ),

            new(
                "builtin-security-rotate",
                "Secret Rotation",
                "Rotate API keys, tokens, and passwords",
                "Security",
                new[] { "security", "secrets", "rotation" },
                true,
                new RunbookTemplateContent
                {
                    Steps = new List<TemplateStepDef>
                    {
                        new() { StepId = "backup", Name = "Backup Current Secrets", ScriptType = "powershell" },
                        new() { StepId = "generate", Name = "Generate New Secrets", ScriptType = "powershell", DependsOn = new[] { "backup" } },
                        new() { StepId = "update", Name = "Update Applications", ScriptType = "powershell", DependsOn = new[] { "generate" }, EnableRetry = true },
                        new() { StepId = "verify", Name = "Verify New Secrets Work", ScriptType = "powershell", DependsOn = new[] { "update" } },
                        new() { StepId = "revoke", Name = "Revoke Old Secrets", ScriptType = "powershell", DependsOn = new[] { "verify" } },
                        new() { StepId = "audit", Name = "Log Rotation Event", ScriptType = "powershell", Condition = "Always", DependsOn = new[] { "revoke" } }
                    },
                    Trigger = new TemplateTriggerDef { Type = "Schedule", CronExpression = "0 2 1 */3 *" }
                }
            )
        };
    }
}
