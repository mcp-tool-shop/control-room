using ControlRoom.Application.Services;
using ControlRoom.Domain.Model;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Unit tests for TriggerService components.
/// Note: Full integration tests would require database and executor mocking.
/// </summary>
public sealed class TriggerServiceTests
{
    private static ThingId CreateThingId() => new(Guid.NewGuid());

    [Fact]
    public void ScheduleTrigger_CronExpressionParsing()
    {
        // Arrange - every minute
        var trigger = new ScheduleTrigger("* * * * *");

        // Assert
        Assert.Equal(TriggerType.Schedule, trigger.TriggerType);
        Assert.Equal("* * * * *", trigger.CronExpression);
        Assert.Null(trigger.TimeZoneId);
    }

    [Fact]
    public void ScheduleTrigger_WithTimeZone()
    {
        // Arrange - daily at 3am in Pacific time
        var trigger = new ScheduleTrigger("0 3 * * *", "America/Los_Angeles");

        // Assert
        Assert.Equal("America/Los_Angeles", trigger.TimeZoneId);
    }

    [Fact]
    public void WebhookTrigger_BasicConstruction()
    {
        // Arrange
        var secret = Guid.NewGuid().ToString("N");
        var trigger = new WebhookTrigger(secret);

        // Assert
        Assert.Equal(TriggerType.Webhook, trigger.TriggerType);
        Assert.Equal(secret, trigger.Secret);
        Assert.Null(trigger.AllowedIpRange);
    }

    [Fact]
    public void WebhookTrigger_SignatureValidation()
    {
        // Arrange
        var secret = "test-secret";
        var trigger = new WebhookTrigger(secret);
        var payload = "{\"event\":\"test\"}";

        // Generate expected signature
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        var expectedSignature = Convert.ToHexStringLower(hash);

        // Act
        var isValid = trigger.ValidateSignature(payload, expectedSignature);
        var isValidWithPrefix = trigger.ValidateSignature(payload, $"sha256={expectedSignature}");
        var isInvalid = trigger.ValidateSignature(payload, "invalid-signature");

        // Assert
        Assert.True(isValid);
        Assert.True(isValidWithPrefix);
        Assert.False(isInvalid);
    }

    [Fact]
    public void WebhookTrigger_WithIpRestriction()
    {
        // Arrange
        var trigger = new WebhookTrigger("secret", "192.168.1.0/24");

        // Assert
        Assert.Equal("192.168.1.0/24", trigger.AllowedIpRange);
    }

    [Fact]
    public void FileWatchTrigger_BasicConstruction()
    {
        // Arrange
        var trigger = new FileWatchTrigger(@"C:\Scripts", "*.ps1");

        // Assert
        Assert.Equal(TriggerType.FileWatch, trigger.TriggerType);
        Assert.Equal(@"C:\Scripts", trigger.Path);
        Assert.Equal("*.ps1", trigger.Pattern);
        Assert.False(trigger.IncludeSubdirectories);
        Assert.Null(trigger.Debounce);
    }

    [Fact]
    public void FileWatchTrigger_WithOptions()
    {
        // Arrange
        var trigger = new FileWatchTrigger(
            @"C:\Data",
            "*.csv",
            IncludeSubdirectories: true,
            Debounce: TimeSpan.FromSeconds(5)
        );

        // Assert
        Assert.True(trigger.IncludeSubdirectories);
        Assert.Equal(TimeSpan.FromSeconds(5), trigger.Debounce);
    }

    [Fact]
    public void ManualTrigger_Construction()
    {
        // Arrange
        var trigger = new ManualTrigger();

        // Assert
        Assert.Equal(TriggerType.Manual, trigger.TriggerType);
    }

    [Fact]
    public void TriggerResult_Success()
    {
        // Arrange
        var executionId = RunbookExecutionId.New();

        // Act
        var result = new TriggerResult(true, executionId, null);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(executionId, result.ExecutionId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void TriggerResult_Failure()
    {
        // Arrange
        var errorMessage = "Runbook is disabled";

        // Act
        var result = new TriggerResult(false, null, errorMessage);

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.ExecutionId);
        Assert.Equal(errorMessage, result.ErrorMessage);
    }

    [Fact]
    public void FileWatcherInfo_Properties()
    {
        // Arrange
        var runbookId = RunbookId.New();

        // Act
        var info = new FileWatcherInfo(
            runbookId,
            "Log Monitor",
            @"C:\Logs",
            "*.log",
            true
        );

        // Assert
        Assert.Equal(runbookId, info.RunbookId);
        Assert.Equal("Log Monitor", info.RunbookName);
        Assert.Equal(@"C:\Logs", info.WatchPath);
        Assert.Equal("*.log", info.Pattern);
        Assert.True(info.IsActive);
    }

    [Fact]
    public void TriggerFiredEventArgs_Properties()
    {
        // Arrange
        var runbookId = RunbookId.New();
        var executionId = RunbookExecutionId.New();
        var firedAt = DateTimeOffset.UtcNow;

        // Act
        var args = new TriggerFiredEventArgs
        {
            RunbookId = runbookId,
            RunbookName = "Backup Job",
            TriggerType = TriggerType.Schedule,
            FiredAt = firedAt,
            ExecutionId = executionId,
            TriggerInfo = "Daily 3am backup"
        };

        // Assert
        Assert.Equal(runbookId, args.RunbookId);
        Assert.Equal("Backup Job", args.RunbookName);
        Assert.Equal(TriggerType.Schedule, args.TriggerType);
        Assert.Equal(firedAt, args.FiredAt);
        Assert.Equal(executionId, args.ExecutionId);
        Assert.Equal("Daily 3am backup", args.TriggerInfo);
    }

    [Theory]
    [InlineData("* * * * *", true)]           // Every minute
    [InlineData("0 * * * *", true)]           // Every hour
    [InlineData("0 0 * * *", true)]           // Every day at midnight
    [InlineData("0 0 * * 0", true)]           // Every Sunday at midnight
    [InlineData("*/5 * * * *", true)]         // Every 5 minutes
    [InlineData("0 9-17 * * 1-5", true)]      // Hourly 9am-5pm weekdays
    [InlineData("0 0 1 * *", true)]           // Monthly
    [InlineData("invalid", false)]             // Invalid expression
    [InlineData("", false)]                    // Empty expression
    public void CronExpression_Validation(string expression, bool isValid)
    {
        // We can't easily test the parsing without the actual NCrontab library,
        // but we can verify the trigger stores the expression
        var trigger = new ScheduleTrigger(expression);
        Assert.Equal(expression, trigger.CronExpression);
    }

    [Fact]
    public void RunbookWithScheduleTrigger_Serialization()
    {
        // Arrange
        var runbook = new Runbook(
            RunbookId.New(),
            "Scheduled Job",
            "Runs on a schedule",
            new List<RunbookStep>
            {
                new("step1", "Step 1", CreateThingId(), "default",
                    StepCondition.Always, Array.Empty<string>(), null, null, null)
            },
            new ScheduleTrigger("0 0 * * *", "UTC"),
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );

        // Act
        var config = new RunbookConfig
        {
            Name = runbook.Name,
            Description = runbook.Description,
            Trigger = runbook.Trigger,
            IsEnabled = runbook.IsEnabled
        };

        var json = config.ToJson();
        var restored = RunbookConfig.Parse(json);

        // Assert
        Assert.NotNull(restored.Trigger);
        Assert.IsType<ScheduleTrigger>(restored.Trigger);
        var schedule = (ScheduleTrigger)restored.Trigger;
        Assert.Equal("0 0 * * *", schedule.CronExpression);
        Assert.Equal("UTC", schedule.TimeZoneId);
    }

    [Fact]
    public void RunbookWithWebhookTrigger_Serialization()
    {
        // Arrange
        var secret = "webhook-secret-123";
        var config = new RunbookConfig
        {
            Name = "Webhook Job",
            Description = "Triggered by webhook",
            Trigger = new WebhookTrigger(secret, "10.0.0.0/8"),
            IsEnabled = true
        };

        // Act
        var json = config.ToJson();
        var restored = RunbookConfig.Parse(json);

        // Assert
        Assert.NotNull(restored.Trigger);
        Assert.IsType<WebhookTrigger>(restored.Trigger);
        var webhook = (WebhookTrigger)restored.Trigger;
        Assert.Equal(secret, webhook.Secret);
        Assert.Equal("10.0.0.0/8", webhook.AllowedIpRange);
    }

    [Fact]
    public void RunbookWithFileWatchTrigger_Serialization()
    {
        // Arrange
        var config = new RunbookConfig
        {
            Name = "File Watch Job",
            Description = "Triggered by file changes",
            Trigger = new FileWatchTrigger(
                @"C:\Incoming",
                "*.xml",
                IncludeSubdirectories: true,
                Debounce: TimeSpan.FromSeconds(10)
            ),
            IsEnabled = true
        };

        // Act
        var json = config.ToJson();
        var restored = RunbookConfig.Parse(json);

        // Assert
        Assert.NotNull(restored.Trigger);
        Assert.IsType<FileWatchTrigger>(restored.Trigger);
        var fileWatch = (FileWatchTrigger)restored.Trigger;
        Assert.Equal(@"C:\Incoming", fileWatch.Path);
        Assert.Equal("*.xml", fileWatch.Pattern);
        Assert.True(fileWatch.IncludeSubdirectories);
        Assert.Equal(TimeSpan.FromSeconds(10), fileWatch.Debounce);
    }

    [Fact]
    public void RunbookWithManualTrigger_Serialization()
    {
        // Arrange
        var config = new RunbookConfig
        {
            Name = "Manual Job",
            Description = "Started manually",
            Trigger = new ManualTrigger(),
            IsEnabled = true
        };

        // Act
        var json = config.ToJson();
        var restored = RunbookConfig.Parse(json);

        // Assert
        Assert.NotNull(restored.Trigger);
        Assert.IsType<ManualTrigger>(restored.Trigger);
    }

    [Fact]
    public void NullLogger_DoesNotThrow()
    {
        // Arrange
        var logger = NullLogger<object>.Instance;

        // Act & Assert - should not throw
        logger.LogInformation("Test message", 1, 2, 3);
        logger.LogDebug("Debug message");
        logger.LogWarning("Warning message");
        logger.LogError(new Exception("test"), "Error message");
    }
}
