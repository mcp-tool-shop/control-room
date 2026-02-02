namespace ControlRoom.Application.Services;

/// <summary>
/// Internal Dogfooding: Tracks internal usage of Control Room itself,
/// logs pain points, and provides power-user shortcuts to minimize friction.
///
/// Checklist items addressed:
/// - Team uses Control Room itself
/// - Pain points logged
/// - Repetitive steps minimized
/// - Power-user shortcuts exist
/// </summary>
public sealed class DogfoodingService
{
    private readonly IDogfoodingRepository _repository;
    private readonly IShortcutRegistry _shortcutRegistry;
    private readonly IUsageAnalytics _analytics;

    public event EventHandler<PainPointLoggedEventArgs>? PainPointLogged;
    public event EventHandler<ShortcutCreatedEventArgs>? ShortcutCreated;
    public event EventHandler<FrictionPatternDetectedEventArgs>? FrictionPatternDetected;

    public DogfoodingService(
        IDogfoodingRepository repository,
        IShortcutRegistry shortcutRegistry,
        IUsageAnalytics analytics)
    {
        _repository = repository;
        _shortcutRegistry = shortcutRegistry;
        _analytics = analytics;
    }

    // ========================================================================
    // DAILY USE: Track Internal Usage
    // ========================================================================

    /// <summary>
    /// Records a usage session for internal team members.
    /// </summary>
    public async Task<UsageSessionResult> RecordUsageSessionAsync(
        string userId,
        UsageSession session,
        CancellationToken cancellationToken = default)
    {
        session.Id = Guid.NewGuid().ToString("N");
        session.UserId = userId;
        session.EndedAt ??= DateTimeOffset.UtcNow;
        session.Duration = session.EndedAt.Value - session.StartedAt;

        // Analyze for friction patterns
        var frictionAnalysis = await AnalyzeSessionForFrictionAsync(session, cancellationToken);
        session.FrictionScore = frictionAnalysis.OverallScore;

        await _repository.SaveSessionAsync(session, cancellationToken);

        // Detect patterns that might warrant shortcuts
        if (frictionAnalysis.SuggestedAutomations.Any())
        {
            foreach (var suggestion in frictionAnalysis.SuggestedAutomations)
            {
                FrictionPatternDetected?.Invoke(this,
                    new FrictionPatternDetectedEventArgs(suggestion));
            }
        }

        return new UsageSessionResult
        {
            SessionId = session.Id,
            Duration = session.Duration,
            ActionsRecorded = session.Actions.Count,
            FrictionScore = session.FrictionScore,
            SuggestedImprovements = frictionAnalysis.SuggestedAutomations
        };
    }

    /// <summary>
    /// Gets usage statistics for the team.
    /// </summary>
    public async Task<UsageStatistics> GetUsageStatisticsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _repository.GetSessionsAsync(from, to, cancellationToken);

        var userStats = sessions
            .GroupBy(s => s.UserId)
            .Select(g => new UserUsageStats
            {
                UserId = g.Key,
                SessionCount = g.Count(),
                TotalDuration = TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks)),
                AverageFrictionScore = g.Average(s => s.FrictionScore),
                MostUsedFeatures = GetMostUsedFeatures(g.SelectMany(s => s.Actions).ToList())
            })
            .ToList();

        var featureUsage = sessions
            .SelectMany(s => s.Actions)
            .GroupBy(a => a.FeatureArea)
            .Select(g => new FeatureUsageStats
            {
                FeatureArea = g.Key,
                TotalUses = g.Count(),
                UniqueUsers = g.Select(a => a.UserId).Distinct().Count(),
                AverageCompletionTime = TimeSpan.FromMilliseconds(
                    g.Where(a => a.CompletionTime.HasValue)
                     .Select(a => a.CompletionTime!.Value.TotalMilliseconds)
                     .DefaultIfEmpty(0)
                     .Average())
            })
            .OrderByDescending(f => f.TotalUses)
            .ToList();

        return new UsageStatistics
        {
            Period = (from, to),
            TotalSessions = sessions.Count,
            UniqueUsers = sessions.Select(s => s.UserId).Distinct().Count(),
            TotalActionsPerformed = sessions.Sum(s => s.Actions.Count),
            AverageSessionDuration = sessions.Any()
                ? TimeSpan.FromTicks((long)sessions.Average(s => s.Duration.Ticks))
                : TimeSpan.Zero,
            OverallFrictionScore = sessions.Any() ? sessions.Average(s => s.FrictionScore) : 0,
            UserStats = userStats,
            FeatureUsage = featureUsage,
            AdoptionTrend = await CalculateAdoptionTrendAsync(from, to, cancellationToken)
        };
    }

    // ========================================================================
    // PAIN POINTS: Log and Track Issues
    // ========================================================================

    /// <summary>
    /// Logs a pain point experienced by a team member.
    /// </summary>
    public async Task<PainPointResult> LogPainPointAsync(
        PainPoint painPoint,
        CancellationToken cancellationToken = default)
    {
        painPoint.Id = Guid.NewGuid().ToString("N");
        painPoint.LoggedAt = DateTimeOffset.UtcNow;
        painPoint.Status = PainPointStatus.New;

        // Check for similar pain points
        var similar = await FindSimilarPainPointsAsync(painPoint, cancellationToken);
        if (similar.Any())
        {
            painPoint.RelatedPainPoints = similar.Select(p => p.Id).ToList();

            // Increase priority if this is a recurring issue
            var occurrences = similar.Count + 1;
            painPoint.Priority = occurrences switch
            {
                >= 5 => PainPointPriority.Critical,
                >= 3 => PainPointPriority.High,
                >= 2 => PainPointPriority.Medium,
                _ => painPoint.Priority
            };
        }

        await _repository.SavePainPointAsync(painPoint, cancellationToken);

        PainPointLogged?.Invoke(this, new PainPointLoggedEventArgs(painPoint));

        return new PainPointResult
        {
            PainPointId = painPoint.Id,
            Priority = painPoint.Priority,
            SimilarIssuesCount = similar.Count,
            SuggestedCategory = await CategorizeAutomaticallyAsync(painPoint, cancellationToken)
        };
    }

    /// <summary>
    /// Gets all tracked pain points with filtering.
    /// </summary>
    public async Task<PainPointReport> GetPainPointReportAsync(
        PainPointFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var allPainPoints = await _repository.GetPainPointsAsync(cancellationToken);

        var filtered = allPainPoints.AsEnumerable();

        if (filter != null)
        {
            if (filter.Status.HasValue)
                filtered = filtered.Where(p => p.Status == filter.Status.Value);
            if (filter.Priority.HasValue)
                filtered = filtered.Where(p => p.Priority == filter.Priority.Value);
            if (filter.Category != null)
                filtered = filtered.Where(p => p.Category == filter.Category);
            if (filter.FeatureArea != null)
                filtered = filtered.Where(p => p.FeatureArea == filter.FeatureArea);
            if (filter.Since.HasValue)
                filtered = filtered.Where(p => p.LoggedAt >= filter.Since.Value);
        }

        var painPoints = filtered.ToList();

        var byCategory = painPoints
            .GroupBy(p => p.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        var byPriority = painPoints
            .GroupBy(p => p.Priority)
            .ToDictionary(g => g.Key, g => g.Count());

        var topRecurring = painPoints
            .Where(p => p.RelatedPainPoints.Any())
            .OrderByDescending(p => p.RelatedPainPoints.Count)
            .Take(10)
            .ToList();

        return new PainPointReport
        {
            TotalPainPoints = painPoints.Count,
            ByCategory = byCategory,
            ByPriority = byPriority,
            TopRecurringIssues = topRecurring,
            RecentPainPoints = painPoints
                .OrderByDescending(p => p.LoggedAt)
                .Take(20)
                .ToList(),
            ResolutionRate = CalculateResolutionRate(painPoints),
            AverageResolutionTime = CalculateAverageResolutionTime(painPoints)
        };
    }

    /// <summary>
    /// Updates the status of a pain point.
    /// </summary>
    public async Task UpdatePainPointStatusAsync(
        string painPointId,
        PainPointStatus newStatus,
        string? resolution = null,
        CancellationToken cancellationToken = default)
    {
        var painPoint = await _repository.GetPainPointAsync(painPointId, cancellationToken);
        if (painPoint == null) return;

        painPoint.Status = newStatus;

        if (newStatus == PainPointStatus.Resolved && resolution != null)
        {
            painPoint.Resolution = resolution;
            painPoint.ResolvedAt = DateTimeOffset.UtcNow;
        }

        await _repository.SavePainPointAsync(painPoint, cancellationToken);
    }

    // ========================================================================
    // FRICTION: Minimize Repetitive Steps
    // ========================================================================

    /// <summary>
    /// Analyzes usage patterns to identify repetitive workflows.
    /// </summary>
    public async Task<FrictionAnalysis> AnalyzeFrictionAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _repository.GetSessionsAsync(from, to, cancellationToken);

        // Find repetitive action sequences
        var actionSequences = sessions
            .SelectMany(s => ExtractActionSequences(s.Actions, 3))
            .GroupBy(seq => string.Join("->", seq.Select(a => a.ActionType)))
            .Where(g => g.Count() >= 3) // Occurred at least 3 times
            .Select(g => new RepetitiveSequence
            {
                Sequence = g.Key.Split("->").ToList(),
                Occurrences = g.Count(),
                AverageTime = TimeSpan.FromMilliseconds(
                    g.SelectMany(seq => seq)
                     .Where(a => a.CompletionTime.HasValue)
                     .Select(a => a.CompletionTime!.Value.TotalMilliseconds)
                     .DefaultIfEmpty(0)
                     .Average()),
                AutomationPotential = CalculateAutomationPotential(g.First().ToList())
            })
            .OrderByDescending(r => r.Occurrences * r.AutomationPotential)
            .Take(10)
            .ToList();

        // Find high-friction actions
        var highFrictionActions = sessions
            .SelectMany(s => s.Actions)
            .Where(a => a.WasAbandoned || a.ErrorOccurred || a.CompletionTime > TimeSpan.FromSeconds(30))
            .GroupBy(a => a.ActionType)
            .Select(g => new HighFrictionAction
            {
                ActionType = g.Key,
                TotalOccurrences = g.Count(),
                AbandonmentRate = (double)g.Count(a => a.WasAbandoned) / g.Count(),
                ErrorRate = (double)g.Count(a => a.ErrorOccurred) / g.Count(),
                AverageCompletionTime = TimeSpan.FromMilliseconds(
                    g.Where(a => a.CompletionTime.HasValue)
                     .Select(a => a.CompletionTime!.Value.TotalMilliseconds)
                     .DefaultIfEmpty(0)
                     .Average())
            })
            .OrderByDescending(a => a.AbandonmentRate + a.ErrorRate)
            .Take(10)
            .ToList();

        return new FrictionAnalysis
        {
            Period = (from, to),
            RepetitiveSequences = actionSequences,
            HighFrictionActions = highFrictionActions,
            OverallFrictionScore = CalculateOverallFrictionScore(sessions.ToList()),
            Recommendations = GenerateFrictionRecommendations(actionSequences, highFrictionActions)
        };
    }

    // ========================================================================
    // SHORTCUTS: Power-User Features
    // ========================================================================

    /// <summary>
    /// Registers a new keyboard shortcut.
    /// </summary>
    public async Task<ShortcutResult> RegisterShortcutAsync(
        ShortcutDefinition shortcut,
        CancellationToken cancellationToken = default)
    {
        // Validate shortcut doesn't conflict
        var existing = await _shortcutRegistry.GetByKeysAsync(shortcut.Keys, cancellationToken);
        if (existing != null)
        {
            return new ShortcutResult
            {
                Success = false,
                Error = $"Shortcut {shortcut.Keys} is already assigned to '{existing.Name}'"
            };
        }

        shortcut.Id = Guid.NewGuid().ToString("N");
        shortcut.CreatedAt = DateTimeOffset.UtcNow;

        await _shortcutRegistry.RegisterAsync(shortcut, cancellationToken);

        ShortcutCreated?.Invoke(this, new ShortcutCreatedEventArgs(shortcut));

        return new ShortcutResult
        {
            Success = true,
            ShortcutId = shortcut.Id,
            Keys = shortcut.Keys
        };
    }

    /// <summary>
    /// Gets all available shortcuts.
    /// </summary>
    public async Task<IReadOnlyList<ShortcutDefinition>> GetShortcutsAsync(
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        var shortcuts = await _shortcutRegistry.GetAllAsync(cancellationToken);

        if (category != null)
        {
            shortcuts = shortcuts.Where(s => s.Category == category).ToList();
        }

        return shortcuts;
    }

    /// <summary>
    /// Creates a quick action (macro) from a sequence of actions.
    /// </summary>
    public async Task<QuickActionResult> CreateQuickActionAsync(
        QuickActionDefinition quickAction,
        CancellationToken cancellationToken = default)
    {
        quickAction.Id = Guid.NewGuid().ToString("N");
        quickAction.CreatedAt = DateTimeOffset.UtcNow;

        // Validate action steps
        var validation = await ValidateQuickActionStepsAsync(quickAction.Steps, cancellationToken);
        if (!validation.IsValid)
        {
            return new QuickActionResult
            {
                Success = false,
                Error = validation.Error
            };
        }

        await _repository.SaveQuickActionAsync(quickAction, cancellationToken);

        // Optionally register a shortcut
        if (quickAction.Shortcut != null)
        {
            await RegisterShortcutAsync(new ShortcutDefinition
            {
                Name = quickAction.Name,
                Keys = quickAction.Shortcut,
                Category = "Quick Actions",
                Description = quickAction.Description,
                ActionId = quickAction.Id
            }, cancellationToken);
        }

        return new QuickActionResult
        {
            Success = true,
            QuickActionId = quickAction.Id,
            EstimatedTimeSaved = EstimateTimeSaved(quickAction.Steps)
        };
    }

    /// <summary>
    /// Executes a quick action.
    /// </summary>
    public async Task<QuickActionExecutionResult> ExecuteQuickActionAsync(
        string quickActionId,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var quickAction = await _repository.GetQuickActionAsync(quickActionId, cancellationToken);
        if (quickAction == null)
        {
            return new QuickActionExecutionResult
            {
                Success = false,
                Error = "Quick action not found"
            };
        }

        var results = new List<QuickActionStepResult>();
        var startTime = DateTimeOffset.UtcNow;

        foreach (var step in quickAction.Steps)
        {
            var stepResult = await ExecuteStepAsync(step, parameters, cancellationToken);
            results.Add(stepResult);

            if (!stepResult.Success && !step.ContinueOnError)
            {
                return new QuickActionExecutionResult
                {
                    Success = false,
                    QuickActionId = quickActionId,
                    StepResults = results,
                    Error = $"Failed at step {step.Order}: {stepResult.Error}",
                    Duration = DateTimeOffset.UtcNow - startTime
                };
            }
        }

        // Track usage
        await _analytics.TrackQuickActionUsageAsync(quickActionId, cancellationToken);

        return new QuickActionExecutionResult
        {
            Success = true,
            QuickActionId = quickActionId,
            StepResults = results,
            Duration = DateTimeOffset.UtcNow - startTime
        };
    }

    /// <summary>
    /// Suggests shortcuts based on user behavior.
    /// </summary>
    public async Task<IReadOnlyList<ShortcutSuggestion>> GetShortcutSuggestionsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var recentSessions = await _repository.GetUserSessionsAsync(
            userId, DateTimeOffset.UtcNow.AddDays(-30), cancellationToken);

        var frequentActions = recentSessions
            .SelectMany(s => s.Actions)
            .GroupBy(a => a.ActionType)
            .Where(g => g.Count() >= 5)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToList();

        var suggestions = new List<ShortcutSuggestion>();
        var existingShortcuts = await _shortcutRegistry.GetAllAsync(cancellationToken);
        var shortcutActions = existingShortcuts.Select(s => s.ActionId).ToHashSet();

        foreach (var action in frequentActions)
        {
            if (!shortcutActions.Contains(action.Key))
            {
                suggestions.Add(new ShortcutSuggestion
                {
                    ActionType = action.Key,
                    UsageCount = action.Count(),
                    SuggestedKeys = SuggestKeyCombination(action.Key),
                    EstimatedTimeSaved = TimeSpan.FromSeconds(action.Count() * 2) // Rough estimate
                });
            }
        }

        return suggestions;
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    private async Task<SessionFrictionAnalysis> AnalyzeSessionForFrictionAsync(
        UsageSession session,
        CancellationToken cancellationToken)
    {
        var analysis = new SessionFrictionAnalysis
        {
            SessionId = session.Id
        };

        // Calculate friction based on various factors
        double frictionScore = 0;

        // Abandoned actions
        var abandonedCount = session.Actions.Count(a => a.WasAbandoned);
        frictionScore += abandonedCount * 10;

        // Errors
        var errorCount = session.Actions.Count(a => a.ErrorOccurred);
        frictionScore += errorCount * 15;

        // Slow actions
        var slowActions = session.Actions.Count(a =>
            a.CompletionTime > TimeSpan.FromSeconds(30));
        frictionScore += slowActions * 5;

        // Repetitive actions (same action within 5 minutes)
        var repetitive = session.Actions
            .GroupBy(a => a.ActionType)
            .Count(g => g.Count() > 3);
        frictionScore += repetitive * 8;

        analysis.OverallScore = Math.Min(frictionScore, 100);

        // Suggest automations for repetitive patterns
        var sequences = ExtractActionSequences(session.Actions, 2);
        var repetitiveSequences = sequences
            .GroupBy(seq => string.Join("->", seq.Select(a => a.ActionType)))
            .Where(g => g.Count() >= 2)
            .ToList();

        foreach (var seq in repetitiveSequences)
        {
            analysis.SuggestedAutomations.Add(new AutomationSuggestion
            {
                Pattern = seq.Key,
                Occurrences = seq.Count(),
                Description = $"Automate the sequence: {seq.Key.Replace("->", " → ")}"
            });
        }

        return analysis;
    }

    private List<List<UserAction>> ExtractActionSequences(
        IReadOnlyList<UserAction> actions,
        int sequenceLength)
    {
        var sequences = new List<List<UserAction>>();

        for (int i = 0; i <= actions.Count - sequenceLength; i++)
        {
            sequences.Add(actions.Skip(i).Take(sequenceLength).ToList());
        }

        return sequences;
    }

    private double CalculateAutomationPotential(List<UserAction> sequence)
    {
        double potential = 0.5; // Base potential

        // Higher potential if all actions completed successfully
        if (sequence.All(a => !a.WasAbandoned && !a.ErrorOccurred))
            potential += 0.2;

        // Higher potential if actions are quick (likely deterministic)
        if (sequence.All(a => a.CompletionTime < TimeSpan.FromSeconds(5)))
            potential += 0.2;

        // Higher potential for certain action types
        var automatableTypes = new HashSet<string>
        {
            "navigation", "form_fill", "button_click", "api_call"
        };

        if (sequence.All(a => automatableTypes.Contains(a.ActionType.ToLower())))
            potential += 0.1;

        return Math.Min(potential, 1.0);
    }

    private double CalculateOverallFrictionScore(List<UsageSession> sessions)
    {
        if (!sessions.Any()) return 0;
        return sessions.Average(s => s.FrictionScore);
    }

    private List<FrictionRecommendation> GenerateFrictionRecommendations(
        List<RepetitiveSequence> repetitive,
        List<HighFrictionAction> highFriction)
    {
        var recommendations = new List<FrictionRecommendation>();

        foreach (var seq in repetitive.Take(3))
        {
            recommendations.Add(new FrictionRecommendation
            {
                Type = RecommendationType.CreateQuickAction,
                Description = $"Create a quick action for: {string.Join(" → ", seq.Sequence)}",
                Impact = RecommendationImpact.High,
                EstimatedTimeSaved = TimeSpan.FromMinutes(seq.Occurrences * 1)
            });
        }

        foreach (var action in highFriction.Take(3))
        {
            if (action.AbandonmentRate > 0.3)
            {
                recommendations.Add(new FrictionRecommendation
                {
                    Type = RecommendationType.ImproveUX,
                    Description = $"Improve UX for '{action.ActionType}' - {action.AbandonmentRate:P0} abandonment rate",
                    Impact = RecommendationImpact.Critical,
                    EstimatedTimeSaved = TimeSpan.Zero // UX improvements are harder to quantify
                });
            }

            if (action.ErrorRate > 0.2)
            {
                recommendations.Add(new FrictionRecommendation
                {
                    Type = RecommendationType.FixBugs,
                    Description = $"Fix errors in '{action.ActionType}' - {action.ErrorRate:P0} error rate",
                    Impact = RecommendationImpact.Critical,
                    EstimatedTimeSaved = TimeSpan.Zero
                });
            }
        }

        return recommendations;
    }

    private async Task<IReadOnlyList<PainPoint>> FindSimilarPainPointsAsync(
        PainPoint painPoint,
        CancellationToken cancellationToken)
    {
        var allPainPoints = await _repository.GetPainPointsAsync(cancellationToken);

        return allPainPoints
            .Where(p => p.Id != painPoint.Id &&
                       (p.FeatureArea == painPoint.FeatureArea ||
                        p.Category == painPoint.Category ||
                        HasSimilarDescription(p.Description, painPoint.Description)))
            .ToList();
    }

    private bool HasSimilarDescription(string desc1, string desc2)
    {
        var words1 = desc1.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = desc2.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var commonWords = words1.Intersect(words2).Count();
        var totalWords = Math.Max(words1.Length, words2.Length);

        return totalWords > 0 && (double)commonWords / totalWords > 0.5;
    }

    private async Task<string> CategorizeAutomaticallyAsync(
        PainPoint painPoint,
        CancellationToken cancellationToken)
    {
        // Simple keyword-based categorization
        var description = painPoint.Description.ToLower();

        if (description.Contains("slow") || description.Contains("performance"))
            return "Performance";
        if (description.Contains("confus") || description.Contains("unclear"))
            return "UX/Clarity";
        if (description.Contains("error") || description.Contains("bug") || description.Contains("crash"))
            return "Bug";
        if (description.Contains("missing") || description.Contains("need") || description.Contains("want"))
            return "Feature Request";

        return "General";
    }

    private double CalculateResolutionRate(List<PainPoint> painPoints)
    {
        if (!painPoints.Any()) return 0;
        var resolved = painPoints.Count(p => p.Status == PainPointStatus.Resolved);
        return (double)resolved / painPoints.Count;
    }

    private TimeSpan? CalculateAverageResolutionTime(List<PainPoint> painPoints)
    {
        var resolved = painPoints
            .Where(p => p.Status == PainPointStatus.Resolved && p.ResolvedAt.HasValue)
            .ToList();

        if (!resolved.Any()) return null;

        var avgTicks = resolved.Average(p => (p.ResolvedAt!.Value - p.LoggedAt).Ticks);
        return TimeSpan.FromTicks((long)avgTicks);
    }

    private async Task<AdoptionTrend> CalculateAdoptionTrendAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // Calculate weekly active users over the period
        var weeks = new List<WeeklyUsage>();
        var current = from;

        while (current < to)
        {
            var weekEnd = current.AddDays(7);
            if (weekEnd > to) weekEnd = to;

            var sessions = await _repository.GetSessionsAsync(current, weekEnd, cancellationToken);

            weeks.Add(new WeeklyUsage
            {
                WeekStart = current,
                UniqueUsers = sessions.Select(s => s.UserId).Distinct().Count(),
                TotalSessions = sessions.Count
            });

            current = weekEnd;
        }

        return new AdoptionTrend
        {
            WeeklyData = weeks,
            TrendDirection = CalculateTrendDirection(weeks)
        };
    }

    private TrendDirectionType CalculateTrendDirection(List<WeeklyUsage> weeks)
    {
        if (weeks.Count < 2) return TrendDirectionType.Stable;

        var firstHalf = weeks.Take(weeks.Count / 2).Average(w => w.UniqueUsers);
        var secondHalf = weeks.Skip(weeks.Count / 2).Average(w => w.UniqueUsers);

        var change = (secondHalf - firstHalf) / Math.Max(firstHalf, 1);

        return change switch
        {
            > 0.1 => TrendDirectionType.Growing,
            < -0.1 => TrendDirectionType.Declining,
            _ => TrendDirectionType.Stable
        };
    }

    private List<string> GetMostUsedFeatures(List<UserAction> actions)
    {
        return actions
            .GroupBy(a => a.FeatureArea)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key)
            .ToList();
    }

    private async Task<QuickActionValidation> ValidateQuickActionStepsAsync(
        IReadOnlyList<QuickActionStep> steps,
        CancellationToken cancellationToken)
    {
        if (!steps.Any())
        {
            return new QuickActionValidation
            {
                IsValid = false,
                Error = "Quick action must have at least one step"
            };
        }

        // Validate each step
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.ActionType))
            {
                return new QuickActionValidation
                {
                    IsValid = false,
                    Error = $"Step {step.Order} has no action type"
                };
            }
        }

        return new QuickActionValidation { IsValid = true };
    }

    private async Task<QuickActionStepResult> ExecuteStepAsync(
        QuickActionStep step,
        Dictionary<string, object>? parameters,
        CancellationToken cancellationToken)
    {
        // Placeholder for actual step execution
        await Task.Delay(10, cancellationToken);

        return new QuickActionStepResult
        {
            StepOrder = step.Order,
            Success = true,
            Duration = TimeSpan.FromMilliseconds(100)
        };
    }

    private TimeSpan EstimateTimeSaved(IReadOnlyList<QuickActionStep> steps)
    {
        // Estimate 3 seconds saved per step on average
        return TimeSpan.FromSeconds(steps.Count * 3);
    }

    private string SuggestKeyCombination(string actionType)
    {
        // Generate a suggested shortcut based on action name
        var firstLetter = actionType.FirstOrDefault(char.IsLetter);
        if (firstLetter == default) firstLetter = 'X';

        return $"Ctrl+Shift+{char.ToUpper(firstLetter)}";
    }
}

// ========================================================================
// SUPPORTING TYPES
// ========================================================================

public class UsageSession
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public IReadOnlyList<UserAction> Actions { get; init; } = Array.Empty<UserAction>();
    public double FrictionScore { get; set; }
}

public class UserAction
{
    public required string ActionType { get; init; }
    public required string FeatureArea { get; init; }
    public string UserId { get; init; } = string.Empty;
    public DateTimeOffset PerformedAt { get; init; }
    public TimeSpan? CompletionTime { get; init; }
    public bool WasAbandoned { get; init; }
    public bool ErrorOccurred { get; init; }
    public Dictionary<string, object> Context { get; init; } = new();
}

public class UsageSessionResult
{
    public required string SessionId { get; init; }
    public TimeSpan Duration { get; init; }
    public int ActionsRecorded { get; init; }
    public double FrictionScore { get; init; }
    public IReadOnlyList<AutomationSuggestion> SuggestedImprovements { get; init; } = Array.Empty<AutomationSuggestion>();
}

public class UsageStatistics
{
    public (DateTimeOffset From, DateTimeOffset To) Period { get; init; }
    public int TotalSessions { get; init; }
    public int UniqueUsers { get; init; }
    public int TotalActionsPerformed { get; init; }
    public TimeSpan AverageSessionDuration { get; init; }
    public double OverallFrictionScore { get; init; }
    public IReadOnlyList<UserUsageStats> UserStats { get; init; } = Array.Empty<UserUsageStats>();
    public IReadOnlyList<FeatureUsageStats> FeatureUsage { get; init; } = Array.Empty<FeatureUsageStats>();
    public AdoptionTrend AdoptionTrend { get; init; } = new();
}

public class UserUsageStats
{
    public required string UserId { get; init; }
    public int SessionCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public double AverageFrictionScore { get; init; }
    public IReadOnlyList<string> MostUsedFeatures { get; init; } = Array.Empty<string>();
}

public class FeatureUsageStats
{
    public required string FeatureArea { get; init; }
    public int TotalUses { get; init; }
    public int UniqueUsers { get; init; }
    public TimeSpan AverageCompletionTime { get; init; }
}

public class AdoptionTrend
{
    public IReadOnlyList<WeeklyUsage> WeeklyData { get; init; } = Array.Empty<WeeklyUsage>();
    public TrendDirectionType TrendDirection { get; init; }
}

public class WeeklyUsage
{
    public DateTimeOffset WeekStart { get; init; }
    public int UniqueUsers { get; init; }
    public int TotalSessions { get; init; }
}

public enum TrendDirectionType
{
    Growing,
    Stable,
    Declining
}

public class PainPoint
{
    public string Id { get; set; } = string.Empty;
    public required string UserId { get; init; }
    public required string Description { get; init; }
    public required string FeatureArea { get; init; }
    public string Category { get; set; } = "General";
    public PainPointPriority Priority { get; set; } = PainPointPriority.Medium;
    public PainPointStatus Status { get; set; } = PainPointStatus.New;
    public DateTimeOffset LoggedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
    public List<string> RelatedPainPoints { get; set; } = new();
    public Dictionary<string, object> Metadata { get; init; } = new();
}

public enum PainPointPriority
{
    Low,
    Medium,
    High,
    Critical
}

public enum PainPointStatus
{
    New,
    Acknowledged,
    InProgress,
    Resolved,
    WontFix
}

public class PainPointResult
{
    public required string PainPointId { get; init; }
    public PainPointPriority Priority { get; init; }
    public int SimilarIssuesCount { get; init; }
    public required string SuggestedCategory { get; init; }
}

public class PainPointFilter
{
    public PainPointStatus? Status { get; init; }
    public PainPointPriority? Priority { get; init; }
    public string? Category { get; init; }
    public string? FeatureArea { get; init; }
    public DateTimeOffset? Since { get; init; }
}

public class PainPointReport
{
    public int TotalPainPoints { get; init; }
    public Dictionary<string, int> ByCategory { get; init; } = new();
    public Dictionary<PainPointPriority, int> ByPriority { get; init; } = new();
    public IReadOnlyList<PainPoint> TopRecurringIssues { get; init; } = Array.Empty<PainPoint>();
    public IReadOnlyList<PainPoint> RecentPainPoints { get; init; } = Array.Empty<PainPoint>();
    public double ResolutionRate { get; init; }
    public TimeSpan? AverageResolutionTime { get; init; }
}

public class SessionFrictionAnalysis
{
    public required string SessionId { get; init; }
    public double OverallScore { get; set; }
    public List<AutomationSuggestion> SuggestedAutomations { get; init; } = new();
}

public class AutomationSuggestion
{
    public required string Pattern { get; init; }
    public int Occurrences { get; init; }
    public required string Description { get; init; }
}

public class FrictionAnalysis
{
    public (DateTimeOffset From, DateTimeOffset To) Period { get; init; }
    public IReadOnlyList<RepetitiveSequence> RepetitiveSequences { get; init; } = Array.Empty<RepetitiveSequence>();
    public IReadOnlyList<HighFrictionAction> HighFrictionActions { get; init; } = Array.Empty<HighFrictionAction>();
    public double OverallFrictionScore { get; init; }
    public IReadOnlyList<FrictionRecommendation> Recommendations { get; init; } = Array.Empty<FrictionRecommendation>();
}

public class RepetitiveSequence
{
    public IReadOnlyList<string> Sequence { get; init; } = Array.Empty<string>();
    public int Occurrences { get; init; }
    public TimeSpan AverageTime { get; init; }
    public double AutomationPotential { get; init; }
}

public class HighFrictionAction
{
    public required string ActionType { get; init; }
    public int TotalOccurrences { get; init; }
    public double AbandonmentRate { get; init; }
    public double ErrorRate { get; init; }
    public TimeSpan AverageCompletionTime { get; init; }
}

public class FrictionRecommendation
{
    public RecommendationType Type { get; init; }
    public required string Description { get; init; }
    public RecommendationImpact Impact { get; init; }
    public TimeSpan EstimatedTimeSaved { get; init; }
}

public enum RecommendationType
{
    CreateQuickAction,
    CreateShortcut,
    ImproveUX,
    FixBugs,
    AddDocumentation
}

public enum RecommendationImpact
{
    Low,
    Medium,
    High,
    Critical
}

public class ShortcutDefinition
{
    public string Id { get; set; } = string.Empty;
    public required string Name { get; init; }
    public required string Keys { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }
    public string? ActionId { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ShortcutResult
{
    public bool Success { get; init; }
    public string? ShortcutId { get; init; }
    public string? Keys { get; init; }
    public string? Error { get; init; }
}

public class QuickActionDefinition
{
    public string Id { get; set; } = string.Empty;
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<QuickActionStep> Steps { get; init; } = Array.Empty<QuickActionStep>();
    public string? Shortcut { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class QuickActionStep
{
    public int Order { get; init; }
    public required string ActionType { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = new();
    public bool ContinueOnError { get; init; }
}

public class QuickActionResult
{
    public bool Success { get; init; }
    public string? QuickActionId { get; init; }
    public string? Error { get; init; }
    public TimeSpan EstimatedTimeSaved { get; init; }
}

public class QuickActionValidation
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
}

public class QuickActionExecutionResult
{
    public bool Success { get; init; }
    public string? QuickActionId { get; init; }
    public IReadOnlyList<QuickActionStepResult> StepResults { get; init; } = Array.Empty<QuickActionStepResult>();
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

public class QuickActionStepResult
{
    public int StepOrder { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

public class ShortcutSuggestion
{
    public required string ActionType { get; init; }
    public int UsageCount { get; init; }
    public required string SuggestedKeys { get; init; }
    public TimeSpan EstimatedTimeSaved { get; init; }
}

public class PainPointLoggedEventArgs : EventArgs
{
    public PainPoint PainPoint { get; }
    public PainPointLoggedEventArgs(PainPoint painPoint) => PainPoint = painPoint;
}

public class ShortcutCreatedEventArgs : EventArgs
{
    public ShortcutDefinition Shortcut { get; }
    public ShortcutCreatedEventArgs(ShortcutDefinition shortcut) => Shortcut = shortcut;
}

public class FrictionPatternDetectedEventArgs : EventArgs
{
    public AutomationSuggestion Suggestion { get; }
    public FrictionPatternDetectedEventArgs(AutomationSuggestion suggestion) => Suggestion = suggestion;
}

// ========================================================================
// REPOSITORY INTERFACES
// ========================================================================

public interface IDogfoodingRepository
{
    Task SaveSessionAsync(UsageSession session, CancellationToken cancellationToken);
    Task<IReadOnlyList<UsageSession>> GetSessionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<IReadOnlyList<UsageSession>> GetUserSessionsAsync(string userId, DateTimeOffset since, CancellationToken cancellationToken);
    Task SavePainPointAsync(PainPoint painPoint, CancellationToken cancellationToken);
    Task<PainPoint?> GetPainPointAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PainPoint>> GetPainPointsAsync(CancellationToken cancellationToken);
    Task SaveQuickActionAsync(QuickActionDefinition quickAction, CancellationToken cancellationToken);
    Task<QuickActionDefinition?> GetQuickActionAsync(string id, CancellationToken cancellationToken);
}

public interface IShortcutRegistry
{
    Task RegisterAsync(ShortcutDefinition shortcut, CancellationToken cancellationToken);
    Task<ShortcutDefinition?> GetByKeysAsync(string keys, CancellationToken cancellationToken);
    Task<IReadOnlyList<ShortcutDefinition>> GetAllAsync(CancellationToken cancellationToken);
}

public interface IUsageAnalytics
{
    Task TrackQuickActionUsageAsync(string quickActionId, CancellationToken cancellationToken);
}
