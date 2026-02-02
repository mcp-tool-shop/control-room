namespace ControlRoom.Application.Services;

/// <summary>
/// Observability UX: Provides comprehensive observability features with excellent UX
/// for understanding system behavior, navigating from alerts to root cause, and
/// tracking historical trends.
///
/// Checklist items addressed:
/// - Can answer what/when/why quickly
/// - Correlated views available
/// - Easy drill-down from alert to root cause
/// - Trends visible
/// - Context preserved
/// </summary>
public sealed class ObservabilityUXService
{
    private readonly IObservabilityRepository _repository;
    private readonly ICorrelationService _correlationService;
    private readonly ITrendAnalyzer _trendAnalyzer;

    public event EventHandler<CorrelatedViewReadyEventArgs>? CorrelatedViewReady;
    public event EventHandler<RootCauseFoundEventArgs>? RootCauseIdentified;

    public ObservabilityUXService(
        IObservabilityRepository repository,
        ICorrelationService correlationService,
        ITrendAnalyzer trendAnalyzer)
    {
        _repository = repository;
        _correlationService = correlationService;
        _trendAnalyzer = trendAnalyzer;
    }

    // ========================================================================
    // COMPREHENSION: What/When/Why
    // ========================================================================

    /// <summary>
    /// Gets a quick summary answering what happened, when, and why.
    /// </summary>
    public async Task<WhatWhenWhySummary> GetQuickSummaryAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        var eventDetails = await _repository.GetEventDetailsAsync(eventId, cancellationToken);
        if (eventDetails == null)
        {
            return new WhatWhenWhySummary
            {
                EventId = eventId,
                Found = false,
                What = "Event not found",
                When = null,
                Why = null
            };
        }

        // Analyze the event to determine the "why"
        var causalAnalysis = await AnalyzeCauseAsync(eventDetails, cancellationToken);

        return new WhatWhenWhySummary
        {
            EventId = eventId,
            Found = true,
            What = eventDetails.Description,
            When = eventDetails.OccurredAt,
            Why = causalAnalysis.ProbableCause,
            Severity = eventDetails.Severity,
            AffectedResources = eventDetails.AffectedResources,
            RelatedEvents = causalAnalysis.RelatedEventIds,
            SuggestedActions = causalAnalysis.SuggestedActions
        };
    }

    /// <summary>
    /// Searches events with natural language query.
    /// </summary>
    public async Task<IReadOnlyList<EventSearchResult>> SearchEventsAsync(
        string query,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var searchCriteria = new EventSearchCriteria
        {
            Query = query,
            FromTime = from ?? DateTimeOffset.UtcNow.AddDays(-7),
            ToTime = to ?? DateTimeOffset.UtcNow,
            MaxResults = maxResults
        };

        return await _repository.SearchEventsAsync(searchCriteria, cancellationToken);
    }

    // ========================================================================
    // CORRELATED VIEWS: Cross-Signal Correlation
    // ========================================================================

    /// <summary>
    /// Creates a correlated view showing related metrics, logs, and traces.
    /// </summary>
    public async Task<CorrelatedView> GetCorrelatedViewAsync(
        string eventId,
        TimeSpan windowBefore,
        TimeSpan windowAfter,
        CancellationToken cancellationToken = default)
    {
        var eventDetails = await _repository.GetEventDetailsAsync(eventId, cancellationToken);
        if (eventDetails == null)
        {
            return new CorrelatedView
            {
                EventId = eventId,
                Found = false
            };
        }

        var from = eventDetails.OccurredAt - windowBefore;
        var to = eventDetails.OccurredAt + windowAfter;

        // Gather all correlated signals
        var correlationTasks = new List<Task>
        {
            GetCorrelatedMetricsAsync(eventDetails, from, to, cancellationToken),
            GetCorrelatedLogsAsync(eventDetails, from, to, cancellationToken),
            GetCorrelatedTracesAsync(eventDetails, from, to, cancellationToken),
            GetCorrelatedAlertsAsync(eventDetails, from, to, cancellationToken)
        };

        await Task.WhenAll(correlationTasks);

        var view = new CorrelatedView
        {
            EventId = eventId,
            Found = true,
            CenterTime = eventDetails.OccurredAt,
            WindowStart = from,
            WindowEnd = to,
            Metrics = await GetCorrelatedMetricsAsync(eventDetails, from, to, cancellationToken),
            Logs = await GetCorrelatedLogsAsync(eventDetails, from, to, cancellationToken),
            Traces = await GetCorrelatedTracesAsync(eventDetails, from, to, cancellationToken),
            Alerts = await GetCorrelatedAlertsAsync(eventDetails, from, to, cancellationToken),
            CorrelationScore = await _correlationService.CalculateCorrelationScoreAsync(eventId, cancellationToken)
        };

        CorrelatedViewReady?.Invoke(this, new CorrelatedViewReadyEventArgs(view));
        return view;
    }

    /// <summary>
    /// Correlates events by a common identifier (trace ID, request ID, etc.).
    /// </summary>
    public async Task<IReadOnlyList<CorrelatedEvent>> GetEventsByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        return await _correlationService.GetCorrelatedEventsAsync(correlationId, cancellationToken);
    }

    // ========================================================================
    // NAVIGATION: Alert to Root Cause Drill-Down
    // ========================================================================

    /// <summary>
    /// Performs drill-down from an alert to identify root cause.
    /// </summary>
    public async Task<RootCauseAnalysis> DrillDownToRootCauseAsync(
        string alertId,
        CancellationToken cancellationToken = default)
    {
        var alert = await _repository.GetAlertAsync(alertId, cancellationToken);
        if (alert == null)
        {
            return new RootCauseAnalysis
            {
                AlertId = alertId,
                Found = false,
                Confidence = 0
            };
        }

        // Build the drill-down path
        var drillDownPath = new List<DrillDownStep>();

        // Step 1: Alert details
        drillDownPath.Add(new DrillDownStep
        {
            Level = 1,
            Type = DrillDownType.Alert,
            Title = "Alert Triggered",
            Description = alert.Message,
            Timestamp = alert.TriggeredAt,
            ResourceId = alertId
        });

        // Step 2: Find triggering metric/log
        var triggerSource = await FindAlertTriggerSourceAsync(alert, cancellationToken);
        if (triggerSource != null)
        {
            drillDownPath.Add(new DrillDownStep
            {
                Level = 2,
                Type = triggerSource.Type,
                Title = $"Trigger: {triggerSource.Name}",
                Description = triggerSource.Description,
                Timestamp = triggerSource.Timestamp,
                ResourceId = triggerSource.ResourceId
            });
        }

        // Step 3: Find related changes/deployments
        var recentChanges = await FindRecentChangesAsync(alert.TriggeredAt, cancellationToken);
        foreach (var change in recentChanges.Take(3))
        {
            drillDownPath.Add(new DrillDownStep
            {
                Level = 3,
                Type = DrillDownType.Change,
                Title = $"Change: {change.Type}",
                Description = change.Description,
                Timestamp = change.OccurredAt,
                ResourceId = change.ChangeId
            });
        }

        // Step 4: Identify probable root cause
        var rootCause = await IdentifyRootCauseAsync(alert, drillDownPath, cancellationToken);

        var analysis = new RootCauseAnalysis
        {
            AlertId = alertId,
            Found = true,
            DrillDownPath = drillDownPath,
            ProbableRootCause = rootCause.Description,
            Confidence = rootCause.Confidence,
            Evidence = rootCause.Evidence,
            SuggestedRemediation = rootCause.Remediation,
            RelatedIncidents = await FindSimilarIncidentsAsync(rootCause, cancellationToken)
        };

        RootCauseIdentified?.Invoke(this, new RootCauseFoundEventArgs(analysis));
        return analysis;
    }

    /// <summary>
    /// Gets suggested next steps for investigation.
    /// </summary>
    public async Task<IReadOnlyList<InvestigationStep>> GetInvestigationStepsAsync(
        string alertId,
        CancellationToken cancellationToken = default)
    {
        var alert = await _repository.GetAlertAsync(alertId, cancellationToken);
        if (alert == null)
        {
            return Array.Empty<InvestigationStep>();
        }

        var steps = new List<InvestigationStep>
        {
            new InvestigationStep
            {
                Order = 1,
                Title = "Check Alert Context",
                Description = "Review the alert details and triggering conditions",
                ActionType = InvestigationActionType.View,
                TargetResourceType = "alert",
                TargetResourceId = alertId
            },
            new InvestigationStep
            {
                Order = 2,
                Title = "View Correlated Signals",
                Description = "Examine metrics, logs, and traces around the alert time",
                ActionType = InvestigationActionType.Correlate,
                TargetResourceType = "correlation",
                TargetResourceId = alertId
            },
            new InvestigationStep
            {
                Order = 3,
                Title = "Check Recent Changes",
                Description = "Review deployments and configuration changes",
                ActionType = InvestigationActionType.Timeline,
                TargetResourceType = "changes",
                TargetResourceId = alert.ResourceId
            },
            new InvestigationStep
            {
                Order = 4,
                Title = "Compare with Baseline",
                Description = "Compare current behavior with historical baseline",
                ActionType = InvestigationActionType.Compare,
                TargetResourceType = "baseline",
                TargetResourceId = alert.ResourceId
            }
        };

        // Add context-specific steps based on alert type
        var additionalSteps = await GetContextSpecificStepsAsync(alert, cancellationToken);
        steps.AddRange(additionalSteps);

        return steps.OrderBy(s => s.Order).ToList();
    }

    // ========================================================================
    // HISTORY: Trends and Context Preservation
    // ========================================================================

    /// <summary>
    /// Gets trend data for a metric over time.
    /// </summary>
    public async Task<TrendData> GetTrendAsync(
        string metricName,
        string resourceId,
        DateTimeOffset from,
        DateTimeOffset to,
        TrendGranularity granularity = TrendGranularity.Hour,
        CancellationToken cancellationToken = default)
    {
        var dataPoints = await _repository.GetMetricTimeSeriesAsync(
            metricName, resourceId, from, to, granularity, cancellationToken);

        var trend = await _trendAnalyzer.AnalyzeTrendAsync(dataPoints, cancellationToken);

        return new TrendData
        {
            MetricName = metricName,
            ResourceId = resourceId,
            From = from,
            To = to,
            Granularity = granularity,
            DataPoints = dataPoints,
            ObservabilityTrendDirection = trend.Direction,
            TrendStrength = trend.Strength,
            Anomalies = trend.Anomalies,
            Forecast = trend.Forecast,
            SeasonalPattern = trend.SeasonalPattern
        };
    }

    /// <summary>
    /// Compares current state with historical baseline.
    /// </summary>
    public async Task<BaselineComparison> CompareWithBaselineAsync(
        string resourceId,
        DateTimeOffset current,
        BaselinePeriod baselinePeriod = BaselinePeriod.LastWeek,
        CancellationToken cancellationToken = default)
    {
        var baseline = await _repository.GetBaselineAsync(resourceId, baselinePeriod, cancellationToken);
        var currentSnapshot = await _repository.GetResourceSnapshotAsync(resourceId, current, cancellationToken);

        var deviations = new List<BaselineDeviation>();

        foreach (var metric in currentSnapshot.Metrics)
        {
            var baselineValue = baseline.Metrics.GetValueOrDefault(metric.Key);
            var deviation = CalculateDeviation(metric.Value, baselineValue);

            if (Math.Abs(deviation.PercentChange) > 10) // Significant deviation
            {
                deviations.Add(deviation);
            }
        }

        return new BaselineComparison
        {
            ResourceId = resourceId,
            CurrentTime = current,
            BaselinePeriod = baselinePeriod,
            OverallHealthScore = CalculateHealthScore(deviations),
            SignificantDeviations = deviations.OrderByDescending(d => Math.Abs(d.PercentChange)).ToList(),
            Recommendation = GenerateBaselineRecommendation(deviations)
        };
    }

    /// <summary>
    /// Preserves investigation context for later resumption.
    /// </summary>
    public async Task<string> SaveInvestigationContextAsync(
        InvestigationContext context,
        CancellationToken cancellationToken = default)
    {
        context.Id = Guid.NewGuid().ToString("N");
        context.SavedAt = DateTimeOffset.UtcNow;

        await _repository.SaveInvestigationContextAsync(context, cancellationToken);
        return context.Id;
    }

    /// <summary>
    /// Loads a previously saved investigation context.
    /// </summary>
    public async Task<InvestigationContext?> LoadInvestigationContextAsync(
        string contextId,
        CancellationToken cancellationToken = default)
    {
        return await _repository.LoadInvestigationContextAsync(contextId, cancellationToken);
    }

    /// <summary>
    /// Lists recent investigation contexts for a user.
    /// </summary>
    public async Task<IReadOnlyList<InvestigationContextSummary>> ListRecentContextsAsync(
        string userId,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return await _repository.ListInvestigationContextsAsync(userId, limit, cancellationToken);
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    private async Task<CausalAnalysis> AnalyzeCauseAsync(
        EventDetails eventDetails,
        CancellationToken cancellationToken)
    {
        // Find events that occurred shortly before
        var precedingEvents = await _repository.GetPrecedingEventsAsync(
            eventDetails.OccurredAt,
            TimeSpan.FromMinutes(30),
            eventDetails.AffectedResources,
            cancellationToken);

        var probableCauses = new List<string>();
        var relatedEvents = new List<string>();

        foreach (var preceding in precedingEvents)
        {
            if (IsPotentialCause(preceding, eventDetails))
            {
                probableCauses.Add($"{preceding.Type}: {preceding.Description}");
                relatedEvents.Add(preceding.Id);
            }
        }

        return new CausalAnalysis
        {
            ProbableCause = probableCauses.FirstOrDefault() ?? "Unable to determine root cause automatically",
            RelatedEventIds = relatedEvents,
            SuggestedActions = GenerateSuggestedActions(eventDetails, probableCauses)
        };
    }

    private bool IsPotentialCause(EventDetails preceding, EventDetails effect)
    {
        // Check if resources overlap
        var resourceOverlap = preceding.AffectedResources
            .Intersect(effect.AffectedResources)
            .Any();

        // Check if event types are causally related
        var causalTypes = new Dictionary<string, HashSet<string>>
        {
            ["deployment"] = new HashSet<string> { "error", "performance_degradation", "outage" },
            ["config_change"] = new HashSet<string> { "error", "behavior_change" },
            ["scaling_event"] = new HashSet<string> { "performance_degradation", "cost_spike" },
            ["security_event"] = new HashSet<string> { "access_denied", "authentication_failure" }
        };

        var isCausalType = causalTypes.TryGetValue(preceding.Type, out var effectTypes) &&
                          effectTypes.Contains(effect.Type);

        return resourceOverlap || isCausalType;
    }

    private List<string> GenerateSuggestedActions(EventDetails eventDetails, List<string> causes)
    {
        var actions = new List<string>();

        switch (eventDetails.Type)
        {
            case "error":
                actions.Add("Check application logs for stack traces");
                actions.Add("Verify service dependencies are healthy");
                actions.Add("Review recent deployments");
                break;
            case "performance_degradation":
                actions.Add("Check resource utilization (CPU, memory, disk)");
                actions.Add("Review database query performance");
                actions.Add("Check for traffic spikes");
                break;
            case "outage":
                actions.Add("Verify infrastructure health");
                actions.Add("Check for regional issues");
                actions.Add("Review auto-scaling policies");
                break;
            default:
                actions.Add("Review correlated signals");
                actions.Add("Check system health dashboard");
                break;
        }

        return actions;
    }

    private async Task<IReadOnlyList<MetricCorrelation>> GetCorrelatedMetricsAsync(
        EventDetails eventDetails,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        return await _correlationService.GetCorrelatedMetricsAsync(
            eventDetails.Id, from, to, cancellationToken);
    }

    private async Task<IReadOnlyList<LogCorrelation>> GetCorrelatedLogsAsync(
        EventDetails eventDetails,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        return await _correlationService.GetCorrelatedLogsAsync(
            eventDetails.Id, from, to, cancellationToken);
    }

    private async Task<IReadOnlyList<TraceCorrelation>> GetCorrelatedTracesAsync(
        EventDetails eventDetails,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        return await _correlationService.GetCorrelatedTracesAsync(
            eventDetails.Id, from, to, cancellationToken);
    }

    private async Task<IReadOnlyList<AlertCorrelation>> GetCorrelatedAlertsAsync(
        EventDetails eventDetails,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        return await _correlationService.GetCorrelatedAlertsAsync(
            eventDetails.Id, from, to, cancellationToken);
    }

    private async Task<TriggerSource?> FindAlertTriggerSourceAsync(
        AlertDetails alert,
        CancellationToken cancellationToken)
    {
        return await _repository.FindAlertTriggerSourceAsync(alert.Id, cancellationToken);
    }

    private async Task<IReadOnlyList<ObservabilityChangeRecord>> FindRecentChangesAsync(
        DateTimeOffset before,
        CancellationToken cancellationToken)
    {
        return await _repository.GetRecentChangesAsync(
            before.AddHours(-24), before, cancellationToken);
    }

    private async Task<RootCauseResult> IdentifyRootCauseAsync(
        AlertDetails alert,
        List<DrillDownStep> drillDownPath,
        CancellationToken cancellationToken)
    {
        // Simple heuristic-based root cause identification
        var changes = drillDownPath.Where(s => s.Type == DrillDownType.Change).ToList();

        if (changes.Any())
        {
            var mostLikelyChange = changes.First();
            return new RootCauseResult
            {
                Description = $"Alert likely caused by: {mostLikelyChange.Title}",
                Confidence = 0.75,
                Evidence = new List<string>
                {
                    $"Change occurred {(alert.TriggeredAt - mostLikelyChange.Timestamp).TotalMinutes:F0} minutes before alert",
                    $"Change affects related resources"
                },
                Remediation = "Consider rolling back the recent change if issue persists"
            };
        }

        return new RootCauseResult
        {
            Description = "Unable to identify specific root cause",
            Confidence = 0.3,
            Evidence = new List<string> { "No recent changes found" },
            Remediation = "Manual investigation recommended"
        };
    }

    private async Task<IReadOnlyList<SimilarIncident>> FindSimilarIncidentsAsync(
        RootCauseResult rootCause,
        CancellationToken cancellationToken)
    {
        return await _repository.FindSimilarIncidentsAsync(rootCause.Description, cancellationToken);
    }

    private async Task<IReadOnlyList<InvestigationStep>> GetContextSpecificStepsAsync(
        AlertDetails alert,
        CancellationToken cancellationToken)
    {
        var steps = new List<InvestigationStep>();
        var baseOrder = 10;

        switch (alert.Category)
        {
            case "performance":
                steps.Add(new InvestigationStep
                {
                    Order = baseOrder++,
                    Title = "Profile Resource Usage",
                    Description = "Examine CPU, memory, and I/O patterns",
                    ActionType = InvestigationActionType.Profile,
                    TargetResourceType = "performance",
                    TargetResourceId = alert.ResourceId
                });
                break;
            case "error":
                steps.Add(new InvestigationStep
                {
                    Order = baseOrder++,
                    Title = "Search Error Logs",
                    Description = "Find related error messages and stack traces",
                    ActionType = InvestigationActionType.Search,
                    TargetResourceType = "logs",
                    TargetResourceId = alert.ResourceId
                });
                break;
            case "security":
                steps.Add(new InvestigationStep
                {
                    Order = baseOrder++,
                    Title = "Review Audit Trail",
                    Description = "Check for suspicious activity patterns",
                    ActionType = InvestigationActionType.Audit,
                    TargetResourceType = "security",
                    TargetResourceId = alert.ResourceId
                });
                break;
        }

        return steps;
    }

    private BaselineDeviation CalculateDeviation(double current, double baseline)
    {
        var percentChange = baseline != 0 ? ((current - baseline) / baseline) * 100 : 0;

        return new BaselineDeviation
        {
            CurrentValue = current,
            BaselineValue = baseline,
            PercentChange = percentChange,
            IsSignificant = Math.Abs(percentChange) > 10,
            Direction = percentChange > 0 ? DeviationDirection.Increase : DeviationDirection.Decrease
        };
    }

    private double CalculateHealthScore(List<BaselineDeviation> deviations)
    {
        if (!deviations.Any()) return 100;

        var avgDeviation = deviations.Average(d => Math.Abs(d.PercentChange));
        return Math.Max(0, 100 - avgDeviation);
    }

    private string GenerateBaselineRecommendation(List<BaselineDeviation> deviations)
    {
        if (!deviations.Any())
            return "All metrics within normal baseline range";

        var critical = deviations.Where(d => Math.Abs(d.PercentChange) > 50).ToList();
        if (critical.Any())
            return $"Critical: {critical.Count} metric(s) significantly deviated from baseline. Immediate investigation recommended.";

        return $"{deviations.Count} metric(s) showing moderate deviation. Monitor closely.";
    }
}

// ========================================================================
// SUPPORTING TYPES
// ========================================================================

public class WhatWhenWhySummary
{
    public required string EventId { get; init; }
    public bool Found { get; init; }
    public required string What { get; init; }
    public DateTimeOffset? When { get; init; }
    public string? Why { get; init; }
    public string? Severity { get; init; }
    public IReadOnlyList<string> AffectedResources { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RelatedEvents { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SuggestedActions { get; init; } = Array.Empty<string>();
}

public class EventSearchCriteria
{
    public required string Query { get; init; }
    public DateTimeOffset FromTime { get; init; }
    public DateTimeOffset ToTime { get; init; }
    public int MaxResults { get; init; } = 50;
}

public class EventSearchResult
{
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? Severity { get; init; }
    public double RelevanceScore { get; init; }
}

public class CorrelatedView
{
    public required string EventId { get; init; }
    public bool Found { get; init; }
    public DateTimeOffset CenterTime { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public IReadOnlyList<MetricCorrelation> Metrics { get; init; } = Array.Empty<MetricCorrelation>();
    public IReadOnlyList<LogCorrelation> Logs { get; init; } = Array.Empty<LogCorrelation>();
    public IReadOnlyList<TraceCorrelation> Traces { get; init; } = Array.Empty<TraceCorrelation>();
    public IReadOnlyList<AlertCorrelation> Alerts { get; init; } = Array.Empty<AlertCorrelation>();
    public double CorrelationScore { get; init; }
}

public class CorrelatedEvent
{
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string CorrelationId { get; init; }
}

public class MetricCorrelation
{
    public required string MetricName { get; init; }
    public double Value { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public double CorrelationStrength { get; init; }
}

public class LogCorrelation
{
    public required string LogId { get; init; }
    public required string Message { get; init; }
    public required string Level { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public class TraceCorrelation
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public required string OperationName { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset StartTime { get; init; }
}

public class AlertCorrelation
{
    public required string AlertId { get; init; }
    public required string AlertName { get; init; }
    public required string Severity { get; init; }
    public DateTimeOffset TriggeredAt { get; init; }
}

public class RootCauseAnalysis
{
    public required string AlertId { get; init; }
    public bool Found { get; init; }
    public IReadOnlyList<DrillDownStep> DrillDownPath { get; init; } = Array.Empty<DrillDownStep>();
    public string? ProbableRootCause { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public string? SuggestedRemediation { get; init; }
    public IReadOnlyList<SimilarIncident> RelatedIncidents { get; init; } = Array.Empty<SimilarIncident>();
}

public class DrillDownStep
{
    public int Level { get; init; }
    public DrillDownType Type { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string ResourceId { get; init; }
}

public enum DrillDownType
{
    Alert,
    Metric,
    Log,
    Trace,
    Change,
    Deployment,
    ConfigChange
}

public class SimilarIncident
{
    public required string IncidentId { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string Resolution { get; init; }
    public double SimilarityScore { get; init; }
}

public class InvestigationStep
{
    public int Order { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public InvestigationActionType ActionType { get; init; }
    public required string TargetResourceType { get; init; }
    public required string TargetResourceId { get; init; }
}

public enum InvestigationActionType
{
    View,
    Correlate,
    Timeline,
    Compare,
    Profile,
    Search,
    Audit
}

public class TrendData
{
    public required string MetricName { get; init; }
    public required string ResourceId { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public TrendGranularity Granularity { get; init; }
    public IReadOnlyList<DataPoint> DataPoints { get; init; } = Array.Empty<DataPoint>();
    public ObservabilityTrendDirection ObservabilityTrendDirection { get; init; }
    public double TrendStrength { get; init; }
    public IReadOnlyList<Anomaly> Anomalies { get; init; } = Array.Empty<Anomaly>();
    public IReadOnlyList<DataPoint>? Forecast { get; init; }
    public SeasonalPattern? SeasonalPattern { get; init; }
}

public enum TrendGranularity
{
    Minute,
    Hour,
    Day,
    Week,
    Month
}

public enum ObservabilityTrendDirection
{
    Increasing,
    Decreasing,
    Stable,
    Volatile
}

public class DataPoint
{
    public DateTimeOffset Timestamp { get; init; }
    public double Value { get; init; }
}

public class Anomaly
{
    public DateTimeOffset Timestamp { get; init; }
    public double Value { get; init; }
    public double ExpectedValue { get; init; }
    public double DeviationScore { get; init; }
}

public class SeasonalPattern
{
    public required string PatternType { get; init; }
    public TimeSpan Period { get; init; }
    public double Confidence { get; init; }
}

public class BaselineComparison
{
    public required string ResourceId { get; init; }
    public DateTimeOffset CurrentTime { get; init; }
    public BaselinePeriod BaselinePeriod { get; init; }
    public double OverallHealthScore { get; init; }
    public IReadOnlyList<BaselineDeviation> SignificantDeviations { get; init; } = Array.Empty<BaselineDeviation>();
    public required string Recommendation { get; init; }
}

public enum BaselinePeriod
{
    LastHour,
    LastDay,
    LastWeek,
    LastMonth
}

public class BaselineDeviation
{
    public string? MetricName { get; init; }
    public double CurrentValue { get; init; }
    public double BaselineValue { get; init; }
    public double PercentChange { get; init; }
    public bool IsSignificant { get; init; }
    public DeviationDirection Direction { get; init; }
}

public enum DeviationDirection
{
    Increase,
    Decrease
}

public class InvestigationContext
{
    public string Id { get; set; } = string.Empty;
    public required string UserId { get; init; }
    public required string AlertId { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset SavedAt { get; set; }
    public IReadOnlyList<string> ViewedResources { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public Dictionary<string, object> CustomData { get; init; } = new();
}

public class InvestigationContextSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string AlertId { get; init; }
    public DateTimeOffset SavedAt { get; init; }
}

public class CorrelatedViewReadyEventArgs : EventArgs
{
    public CorrelatedView View { get; }
    public CorrelatedViewReadyEventArgs(CorrelatedView view) => View = view;
}

public class RootCauseFoundEventArgs : EventArgs
{
    public RootCauseAnalysis Analysis { get; }
    public RootCauseFoundEventArgs(RootCauseAnalysis analysis) => Analysis = analysis;
}

// ========================================================================
// REPOSITORY INTERFACES
// ========================================================================

public interface IObservabilityRepository
{
    Task<EventDetails?> GetEventDetailsAsync(string eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventSearchResult>> SearchEventsAsync(EventSearchCriteria criteria, CancellationToken cancellationToken);
    Task<AlertDetails?> GetAlertAsync(string alertId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventDetails>> GetPrecedingEventsAsync(DateTimeOffset before, TimeSpan window, IReadOnlyList<string> resources, CancellationToken cancellationToken);
    Task<TriggerSource?> FindAlertTriggerSourceAsync(string alertId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ObservabilityChangeRecord>> GetRecentChangesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<IReadOnlyList<SimilarIncident>> FindSimilarIncidentsAsync(string description, CancellationToken cancellationToken);
    Task<IReadOnlyList<DataPoint>> GetMetricTimeSeriesAsync(string metricName, string resourceId, DateTimeOffset from, DateTimeOffset to, TrendGranularity granularity, CancellationToken cancellationToken);
    Task<BaselineData> GetBaselineAsync(string resourceId, BaselinePeriod period, CancellationToken cancellationToken);
    Task<ResourceSnapshot> GetResourceSnapshotAsync(string resourceId, DateTimeOffset at, CancellationToken cancellationToken);
    Task SaveInvestigationContextAsync(InvestigationContext context, CancellationToken cancellationToken);
    Task<InvestigationContext?> LoadInvestigationContextAsync(string contextId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InvestigationContextSummary>> ListInvestigationContextsAsync(string userId, int limit, CancellationToken cancellationToken);
}

public interface ICorrelationService
{
    Task<double> CalculateCorrelationScoreAsync(string eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CorrelatedEvent>> GetCorrelatedEventsAsync(string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MetricCorrelation>> GetCorrelatedMetricsAsync(string eventId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<IReadOnlyList<LogCorrelation>> GetCorrelatedLogsAsync(string eventId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<IReadOnlyList<TraceCorrelation>> GetCorrelatedTracesAsync(string eventId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<IReadOnlyList<AlertCorrelation>> GetCorrelatedAlertsAsync(string eventId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public interface ITrendAnalyzer
{
    Task<TrendAnalysisResult> AnalyzeTrendAsync(IReadOnlyList<DataPoint> dataPoints, CancellationToken cancellationToken);
}

public class TrendAnalysisResult
{
    public ObservabilityTrendDirection Direction { get; init; }
    public double Strength { get; init; }
    public IReadOnlyList<Anomaly> Anomalies { get; init; } = Array.Empty<Anomaly>();
    public IReadOnlyList<DataPoint>? Forecast { get; init; }
    public SeasonalPattern? SeasonalPattern { get; init; }
}

public class EventDetails
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string Severity { get; init; }
    public IReadOnlyList<string> AffectedResources { get; init; } = Array.Empty<string>();
}

public class AlertDetails
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Message { get; init; }
    public required string Category { get; init; }
    public required string Severity { get; init; }
    public required string ResourceId { get; init; }
    public DateTimeOffset TriggeredAt { get; init; }
}

public class TriggerSource
{
    public DrillDownType Type { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string ResourceId { get; init; }
}

public class ObservabilityChangeRecord
{
    public required string ChangeId { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public class CausalAnalysis
{
    public required string ProbableCause { get; init; }
    public IReadOnlyList<string> RelatedEventIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SuggestedActions { get; init; } = Array.Empty<string>();
}

public class RootCauseResult
{
    public required string Description { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public required string Remediation { get; init; }
}

public class BaselineData
{
    public Dictionary<string, double> Metrics { get; init; } = new();
}

public class ResourceSnapshot
{
    public Dictionary<string, double> Metrics { get; init; } = new();
}
