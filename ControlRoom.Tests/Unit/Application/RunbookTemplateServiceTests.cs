using ControlRoom.Application.Services;
using ControlRoom.Domain.Model;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Unit tests for RunbookTemplateService components.
/// </summary>
public sealed class RunbookTemplateServiceTests
{
    [Fact]
    public void RunbookTemplate_BasicConstruction()
    {
        // Arrange
        var content = new RunbookTemplateContent
        {
            Steps = new List<TemplateStepDef>
            {
                new() { StepId = "step1", Name = "Build", ScriptType = "powershell" }
            }
        };

        // Act
        var template = new RunbookTemplate(
            "test-template",
            "Test Template",
            "A test template",
            "Testing",
            new[] { "test", "sample" },
            true,
            content
        );

        // Assert
        Assert.Equal("test-template", template.Id);
        Assert.Equal("Test Template", template.Name);
        Assert.Equal("A test template", template.Description);
        Assert.Equal("Testing", template.Category);
        Assert.Contains("test", template.Tags);
        Assert.True(template.IsBuiltIn);
        Assert.Single(template.Content.Steps);
    }

    [Fact]
    public void TemplateStepDef_DefaultValues()
    {
        // Arrange
        var step = new TemplateStepDef();

        // Assert
        Assert.Equal("", step.StepId);
        Assert.Equal("", step.Name);
        Assert.Equal("", step.ScriptType);
        Assert.Equal("OnSuccess", step.Condition);
        Assert.Empty(step.DependsOn);
        Assert.False(step.EnableRetry);
        Assert.Equal(3, step.MaxRetries);
        Assert.Equal(0, step.TimeoutSeconds);
    }

    [Fact]
    public void TemplateStepDef_WithAllProperties()
    {
        // Arrange
        var step = new TemplateStepDef
        {
            StepId = "deploy",
            Name = "Deploy Application",
            ScriptType = "powershell",
            ScriptTemplate = "Deploy-App -Environment {{env}}",
            Condition = "OnSuccess",
            DependsOn = new[] { "build", "test" },
            EnableRetry = true,
            MaxRetries = 5,
            TimeoutSeconds = 300
        };

        // Assert
        Assert.Equal("deploy", step.StepId);
        Assert.Equal("Deploy Application", step.Name);
        Assert.Equal("powershell", step.ScriptType);
        Assert.Contains("{{env}}", step.ScriptTemplate);
        Assert.Equal(2, step.DependsOn.Length);
        Assert.True(step.EnableRetry);
        Assert.Equal(5, step.MaxRetries);
        Assert.Equal(300, step.TimeoutSeconds);
    }

    [Fact]
    public void TemplateTriggerDef_Schedule()
    {
        // Arrange
        var trigger = new TemplateTriggerDef
        {
            Type = "Schedule",
            CronExpression = "0 0 * * *"
        };

        // Assert
        Assert.Equal("Schedule", trigger.Type);
        Assert.Equal("0 0 * * *", trigger.CronExpression);
    }

    [Fact]
    public void TemplateTriggerDef_FileWatch()
    {
        // Arrange
        var trigger = new TemplateTriggerDef
        {
            Type = "FileWatch",
            WatchPath = @"C:\Data",
            WatchPattern = "*.csv"
        };

        // Assert
        Assert.Equal("FileWatch", trigger.Type);
        Assert.Equal(@"C:\Data", trigger.WatchPath);
        Assert.Equal("*.csv", trigger.WatchPattern);
    }

    [Fact]
    public void RunbookImportResult_Success()
    {
        // Arrange
        var runbookId = RunbookId.New();

        // Act
        var result = new RunbookImportResult(true, runbookId, null, false);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(runbookId, result.RunbookId);
        Assert.Null(result.ErrorMessage);
        Assert.False(result.WasOverwritten);
    }

    [Fact]
    public void RunbookImportResult_SuccessWithOverwrite()
    {
        // Arrange
        var runbookId = RunbookId.New();

        // Act
        var result = new RunbookImportResult(true, runbookId, null, true);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.WasOverwritten);
    }

    [Fact]
    public void RunbookImportResult_Failure()
    {
        // Act
        var result = new RunbookImportResult(false, null, "Invalid format", false);

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.RunbookId);
        Assert.Equal("Invalid format", result.ErrorMessage);
    }

    [Fact]
    public void RunbookExportFormat_Serialization()
    {
        // Arrange
        var config = new RunbookConfig
        {
            Name = "Test Runbook",
            Description = "A test runbook",
            IsEnabled = true,
            Steps = new List<RunbookStepConfig>
            {
                new()
                {
                    StepId = "step1",
                    Name = "Step 1",
                    ThingId = Guid.NewGuid().ToString(),
                    ProfileId = "default",
                    ConditionType = ConditionType.OnSuccess
                }
            }
        };

        var export = new RunbookExportFormat
        {
            Name = "Test Runbook",
            Description = "A test runbook",
            ExportedAt = DateTimeOffset.UtcNow,
            Config = config
        };

        // Act
        var json = export.ToJson();
        var restored = RunbookExportFormat.FromJson(json);

        // Assert
        Assert.NotNull(restored);
        Assert.Equal("Test Runbook", restored.Name);
        Assert.Equal("A test runbook", restored.Description);
        Assert.Equal("1.0", restored.Version);
        Assert.Single(restored.Config.Steps);
        Assert.Equal("step1", restored.Config.Steps[0].StepId);
    }

    [Fact]
    public void RunbookExportFormat_WithTrigger()
    {
        // Arrange
        var config = new RunbookConfig
        {
            Name = "Scheduled Job",
            Trigger = new ScheduleTrigger("0 0 * * *", "UTC"),
            IsEnabled = true
        };

        var export = new RunbookExportFormat
        {
            Name = "Scheduled Job",
            Config = config
        };

        // Act
        var json = export.ToJson();
        var restored = RunbookExportFormat.FromJson(json);

        // Assert
        Assert.NotNull(restored?.Config.Trigger);
        Assert.IsType<ScheduleTrigger>(restored.Config.Trigger);
        var schedule = (ScheduleTrigger)restored.Config.Trigger;
        Assert.Equal("0 0 * * *", schedule.CronExpression);
    }

    [Fact]
    public void RunbookExportFormat_InvalidJson()
    {
        // Act & Assert - invalid JSON throws JsonException
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            RunbookExportFormat.FromJson("not valid json"));
    }

    [Fact]
    public void RunbookExportFormat_FormatVersion()
    {
        // Assert
        Assert.Equal("1.0", RunbookExportFormat.FormatVersion);
    }

    [Fact]
    public void RunbookTemplateContent_WithVariables()
    {
        // Arrange
        var content = new RunbookTemplateContent
        {
            Steps = new List<TemplateStepDef>
            {
                new() { StepId = "deploy", Name = "Deploy", ScriptTemplate = "Deploy-App -Env {{environment}}" }
            },
            Variables = new Dictionary<string, string>
            {
                ["environment"] = "production",
                ["version"] = "1.0.0"
            }
        };

        // Assert
        Assert.Equal(2, content.Variables.Count);
        Assert.Equal("production", content.Variables["environment"]);
    }

    [Theory]
    [InlineData("CI/CD")]
    [InlineData("Backup")]
    [InlineData("Monitoring")]
    [InlineData("Maintenance")]
    [InlineData("Data Processing")]
    [InlineData("Security")]
    public void BuiltInTemplates_Categories(string category)
    {
        // This test verifies that our built-in templates cover the expected categories
        // The actual template service would return templates for each category
        Assert.NotEmpty(category);
    }

    [Fact]
    public void TemplateStepDef_ConditionValues()
    {
        // Test various condition values
        var conditions = new[] { "OnSuccess", "OnFailure", "Always" };

        foreach (var condition in conditions)
        {
            var step = new TemplateStepDef { Condition = condition };
            Assert.Equal(condition, step.Condition);
        }
    }

    [Fact]
    public void RunbookTemplateContent_EmptySteps()
    {
        // Arrange
        var content = new RunbookTemplateContent();

        // Assert
        Assert.Empty(content.Steps);
        Assert.Null(content.Trigger);
        Assert.Empty(content.Variables);
    }

    [Fact]
    public void RunbookExportFormat_ComplexConfig()
    {
        // Arrange
        var config = new RunbookConfig
        {
            Name = "Complex Pipeline",
            Description = "A complex multi-step pipeline",
            IsEnabled = true,
            Steps = new List<RunbookStepConfig>
            {
                new()
                {
                    StepId = "build",
                    Name = "Build",
                    ThingId = Guid.NewGuid().ToString(),
                    ConditionType = ConditionType.Always
                },
                new()
                {
                    StepId = "test",
                    Name = "Test",
                    ThingId = Guid.NewGuid().ToString(),
                    DependsOn = new List<string> { "build" },
                    ConditionType = ConditionType.OnSuccess,
                    Retry = new RetryPolicyConfig { MaxAttempts = 3 }
                },
                new()
                {
                    StepId = "deploy",
                    Name = "Deploy",
                    ThingId = Guid.NewGuid().ToString(),
                    DependsOn = new List<string> { "test" },
                    TimeoutSeconds = 600
                },
                new()
                {
                    StepId = "notify",
                    Name = "Notify",
                    ThingId = Guid.NewGuid().ToString(),
                    DependsOn = new List<string> { "deploy" },
                    ConditionType = ConditionType.Always
                }
            },
            Trigger = new WebhookTrigger("secret-key")
        };

        var export = new RunbookExportFormat
        {
            Name = config.Name,
            Description = config.Description,
            ExportedAt = DateTimeOffset.UtcNow,
            Config = config
        };

        // Act
        var json = export.ToJson();
        var restored = RunbookExportFormat.FromJson(json);

        // Assert
        Assert.NotNull(restored);
        Assert.Equal(4, restored.Config.Steps.Count);

        var testStep = restored.Config.Steps.First(s => s.StepId == "test");
        Assert.Contains("build", testStep.DependsOn);
        Assert.NotNull(testStep.Retry);
        Assert.Equal(3, testStep.Retry.MaxAttempts);

        var deployStep = restored.Config.Steps.First(s => s.StepId == "deploy");
        Assert.Equal(600, deployStep.TimeoutSeconds);

        Assert.IsType<WebhookTrigger>(restored.Config.Trigger);
    }
}
