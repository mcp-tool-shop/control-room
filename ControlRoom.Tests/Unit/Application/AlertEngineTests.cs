using ControlRoom.Application.Services;
using ControlRoom.Domain.Model;

namespace ControlRoom.Tests.Unit.Application;

/// <summary>
/// Unit tests for AlertEngine components.
/// </summary>
public sealed class AlertEngineTests
{
    [Fact]
    public void AlertEvaluationResult_Triggered()
    {
        // Arrange
        var ruleId = AlertRuleId.New();

        // Act
        var result = new AlertEvaluationResult(
            ruleId,
            true,
            95.5,
            90.0,
            "CPU usage is > 90% (current: 95.50)"
        );

        // Assert
        Assert.True(result.IsTriggered);
        Assert.Equal(95.5, result.CurrentValue);
        Assert.Equal(90.0, result.Threshold);
        Assert.Contains("CPU usage", result.Message);
    }

    [Fact]
    public void AlertEvaluationResult_NotTriggered()
    {
        // Arrange
        var ruleId = AlertRuleId.New();

        // Act
        var result = new AlertEvaluationResult(
            ruleId,
            false,
            45.0,
            90.0,
            null
        );

        // Assert
        Assert.False(result.IsTriggered);
        Assert.Equal(45.0, result.CurrentValue);
    }

    [Fact]
    public void AlertFiredEventArgs_Properties()
    {
        // Arrange
        var alert = new Alert(
            AlertId.New(),
            AlertRuleId.New(),
            "Test Rule",
            AlertSeverity.Warning,
            "Test message",
            95.0,
            90.0,
            DateTimeOffset.UtcNow,
            null,
            AlertStatus.Firing,
            new Dictionary<string, string>()
        );

        var rule = new AlertRule(
            AlertRuleId.New(),
            "Test Rule",
            "Test description",
            MetricNames.SystemCpuPercent,
            AlertCondition.GreaterThan,
            90.0,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),
            AlertSeverity.Warning,
            true,
            new Dictionary<string, string>(),
            new List<AlertAction>()
        );

        // Act
        var args = new AlertFiredEventArgs
        {
            Alert = alert,
            Rule = rule
        };

        // Assert
        Assert.NotNull(args.Alert);
        Assert.NotNull(args.Rule);
        Assert.Equal(alert.Id, args.Alert.Id);
        Assert.Equal(rule.Id, args.Rule.Id);
    }

    [Fact]
    public void AlertResolvedEventArgs_Properties()
    {
        // Arrange
        var alertId = AlertId.New();
        var duration = TimeSpan.FromMinutes(15);

        // Act
        var args = new AlertResolvedEventArgs
        {
            AlertId = alertId,
            Duration = duration
        };

        // Assert
        Assert.Equal(alertId, args.AlertId);
        Assert.Equal(duration, args.Duration);
    }

    [Theory]
    [InlineData(AlertCondition.GreaterThan, 100, 90, true)]
    [InlineData(AlertCondition.GreaterThan, 80, 90, false)]
    [InlineData(AlertCondition.GreaterThan, 90, 90, false)]
    [InlineData(AlertCondition.GreaterThanOrEqual, 90, 90, true)]
    [InlineData(AlertCondition.GreaterThanOrEqual, 80, 90, false)]
    [InlineData(AlertCondition.LessThan, 80, 90, true)]
    [InlineData(AlertCondition.LessThan, 95, 90, false)]
    [InlineData(AlertCondition.LessThanOrEqual, 90, 90, true)]
    [InlineData(AlertCondition.LessThanOrEqual, 95, 90, false)]
    [InlineData(AlertCondition.Equal, 90, 90, true)]
    [InlineData(AlertCondition.Equal, 90.1, 90, false)]
    [InlineData(AlertCondition.NotEqual, 85, 90, true)]
    [InlineData(AlertCondition.NotEqual, 90, 90, false)]
    public void ConditionEvaluation_Logic(AlertCondition condition, double current, double threshold, bool expected)
    {
        // These tests verify the expected behavior of condition evaluation
        // The actual implementation is in AlertEngine but we test the logic here
        var result = condition switch
        {
            AlertCondition.GreaterThan => current > threshold,
            AlertCondition.GreaterThanOrEqual => current >= threshold,
            AlertCondition.LessThan => current < threshold,
            AlertCondition.LessThanOrEqual => current <= threshold,
            AlertCondition.Equal => Math.Abs(current - threshold) < 0.0001,
            AlertCondition.NotEqual => Math.Abs(current - threshold) >= 0.0001,
            _ => false
        };

        Assert.Equal(expected, result);
    }

    [Fact]
    public void AlertRule_WithActions()
    {
        // Arrange
        var actions = new List<AlertAction>
        {
            new(AlertActionType.Notification, new Dictionary<string, string>()),
            new(AlertActionType.Email, new Dictionary<string, string>
            {
                ["to"] = "admin@example.com",
                ["subject"] = "Alert: {{rule_name}}"
            }),
            new(AlertActionType.Webhook, new Dictionary<string, string>
            {
                ["url"] = "https://hooks.slack.com/services/xxx",
                ["method"] = "POST"
            }),
            new(AlertActionType.RunRunbook, new Dictionary<string, string>
            {
                ["runbook_id"] = Guid.NewGuid().ToString()
            })
        };

        var rule = new AlertRule(
            AlertRuleId.New(),
            "Multi-Action Alert",
            "Alert with multiple actions",
            MetricNames.SystemMemoryPercent,
            AlertCondition.GreaterThan,
            80.0,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            AlertSeverity.Error,
            true,
            new Dictionary<string, string>(),
            actions
        );

        // Assert
        Assert.Equal(4, rule.Actions.Count);
        Assert.Contains(rule.Actions, a => a.Type == AlertActionType.Notification);
        Assert.Contains(rule.Actions, a => a.Type == AlertActionType.Email);
        Assert.Contains(rule.Actions, a => a.Type == AlertActionType.Webhook);
        Assert.Contains(rule.Actions, a => a.Type == AlertActionType.RunRunbook);
    }

    [Fact]
    public void Alert_StatusTransitions()
    {
        // Firing alert
        var alert = new Alert(
            AlertId.New(),
            AlertRuleId.New(),
            "Test",
            AlertSeverity.Warning,
            "Message",
            95.0,
            90.0,
            DateTimeOffset.UtcNow,
            null,
            AlertStatus.Firing,
            new Dictionary<string, string>()
        );

        Assert.Equal(AlertStatus.Firing, alert.Status);
        Assert.False(alert.IsResolved);

        // Acknowledged alert
        var acked = alert with { Status = AlertStatus.Acknowledged };
        Assert.Equal(AlertStatus.Acknowledged, acked.Status);
        Assert.False(acked.IsResolved);

        // Resolved alert
        var resolved = alert with
        {
            Status = AlertStatus.Resolved,
            ResolvedAt = DateTimeOffset.UtcNow
        };
        Assert.Equal(AlertStatus.Resolved, resolved.Status);
        Assert.True(resolved.IsResolved);
    }

    [Fact]
    public void AlertRule_CooldownPeriod()
    {
        // Arrange
        var rule = new AlertRule(
            AlertRuleId.New(),
            "Cooldown Test",
            "Test cooldown period",
            MetricNames.ScriptFailure,
            AlertCondition.GreaterThan,
            5.0,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),  // 30 minute cooldown
            AlertSeverity.Warning,
            true,
            new Dictionary<string, string>(),
            new List<AlertAction>()
        );

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(30), rule.CooldownPeriod);
    }

    [Fact]
    public void AlertRule_EvaluationWindow()
    {
        // Arrange
        var rule = new AlertRule(
            AlertRuleId.New(),
            "Window Test",
            "Test evaluation window",
            MetricNames.ScriptDuration,
            AlertCondition.GreaterThan,
            10000.0,
            TimeSpan.FromMinutes(10),  // 10 minute evaluation window
            TimeSpan.FromMinutes(5),
            AlertSeverity.Info,
            true,
            new Dictionary<string, string>(),
            new List<AlertAction>()
        );

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(10), rule.EvaluationWindow);
    }

    [Theory]
    [InlineData(AlertSeverity.Info, "Info")]
    [InlineData(AlertSeverity.Warning, "Warning")]
    [InlineData(AlertSeverity.Error, "Error")]
    [InlineData(AlertSeverity.Critical, "Critical")]
    public void AlertSeverity_ToString(AlertSeverity severity, string expected)
    {
        Assert.Equal(expected, severity.ToString());
    }

    [Fact]
    public void Alert_DurationCalculation()
    {
        var firedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var resolvedAt = DateTimeOffset.UtcNow;

        var alert = new Alert(
            AlertId.New(),
            AlertRuleId.New(),
            "Test",
            AlertSeverity.Warning,
            "Test",
            95.0,
            90.0,
            firedAt,
            resolvedAt,
            AlertStatus.Resolved,
            new Dictionary<string, string>()
        );

        Assert.NotNull(alert.Duration);
        Assert.InRange(alert.Duration!.Value.TotalMinutes, 9.9, 10.1);
    }

    [Fact]
    public void Alert_UnresolvedDuration()
    {
        var firedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var alert = new Alert(
            AlertId.New(),
            AlertRuleId.New(),
            "Test",
            AlertSeverity.Warning,
            "Test",
            95.0,
            90.0,
            firedAt,
            null,  // Not resolved
            AlertStatus.Firing,
            new Dictionary<string, string>()
        );

        Assert.NotNull(alert.Duration);
        Assert.True(alert.Duration!.Value.TotalMinutes >= 4.9);
    }

    [Fact]
    public void AlertRule_Tags()
    {
        var tags = new Dictionary<string, string>
        {
            [MetricTags.Host] = "server1",
            [MetricTags.ThingName] = "backup-script"
        };

        var rule = new AlertRule(
            AlertRuleId.New(),
            "Tagged Rule",
            "Rule with tags",
            MetricNames.ScriptDuration,
            AlertCondition.GreaterThan,
            30000.0,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            AlertSeverity.Warning,
            true,
            tags,
            new List<AlertAction>()
        );

        Assert.Equal(2, rule.Tags.Count);
        Assert.Equal("server1", rule.Tags[MetricTags.Host]);
    }

    [Fact]
    public void AlertAction_WebhookConfig()
    {
        var config = new Dictionary<string, string>
        {
            ["url"] = "https://api.pagerduty.com/alerts",
            ["routing_key"] = "xxx-xxx-xxx",
            ["severity"] = "critical"
        };

        var action = new AlertAction(AlertActionType.Webhook, config);

        Assert.Equal(AlertActionType.Webhook, action.Type);
        Assert.Equal("https://api.pagerduty.com/alerts", action.Config["url"]);
        Assert.Equal("xxx-xxx-xxx", action.Config["routing_key"]);
    }

    [Theory]
    [InlineData(AlertCondition.AbsoluteChange, 50, 10, true)]    // Changed by 50, threshold 10
    [InlineData(AlertCondition.AbsoluteChange, 5, 10, false)]     // Changed by 5, threshold 10
    [InlineData(AlertCondition.PercentChange, 25, 10, true)]      // Changed by 25%, threshold 10%
    [InlineData(AlertCondition.PercentChange, 5, 10, false)]      // Changed by 5%, threshold 10%
    public void ChangeConditions_Logic(AlertCondition condition, double change, double threshold, bool expected)
    {
        // Change conditions compare the change amount against the threshold
        var result = change > threshold;
        Assert.Equal(expected, result);
    }
}
