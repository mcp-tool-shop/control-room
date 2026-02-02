namespace ControlRoom.Application.Services;

/// <summary>
/// Product Narrative: Manages the product's value proposition, ideal customer profile,
/// and demo experiences to ensure clear communication and logical story flow.
///
/// Checklist items addressed:
/// - One-sentence value prop
/// - Clear ICP (Ideal Customer Profile)
/// - Logical story flow (for demos)
/// - Avoid feature dumping
/// </summary>
public sealed class ProductNarrativeService
{
    private readonly IProductNarrativeRepository _repository;
    private readonly IDemoOrchestrator _demoOrchestrator;

    public event EventHandler<NarrativeUpdatedEventArgs>? NarrativeUpdated;
    public event EventHandler<DemoCompletedEventArgs>? DemoCompleted;

    public ProductNarrativeService(
        IProductNarrativeRepository repository,
        IDemoOrchestrator demoOrchestrator)
    {
        _repository = repository;
        _demoOrchestrator = demoOrchestrator;
    }

    // ========================================================================
    // VALUE PROPOSITION: One-Sentence Clarity
    // ========================================================================

    /// <summary>
    /// Gets the current value proposition.
    /// </summary>
    public async Task<ValueProposition> GetValuePropositionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetValuePropositionAsync(cancellationToken)
            ?? GetDefaultValueProposition();
    }

    /// <summary>
    /// Updates the value proposition with validation.
    /// </summary>
    public async Task<ValuePropositionResult> UpdateValuePropositionAsync(
        ValueProposition valueProp,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateValueProposition(valueProp);
        if (!validation.IsValid)
        {
            return new ValuePropositionResult
            {
                Success = false,
                Errors = validation.Errors
            };
        }

        valueProp.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveValuePropositionAsync(valueProp, cancellationToken);

        NarrativeUpdated?.Invoke(this, new NarrativeUpdatedEventArgs("ValueProposition"));

        return new ValuePropositionResult
        {
            Success = true,
            ValueProposition = valueProp
        };
    }

    /// <summary>
    /// Generates a value proposition based on product capabilities.
    /// </summary>
    public async Task<ValuePropositionSuggestion> SuggestValuePropositionAsync(
        IReadOnlyList<string> keyCapabilities,
        string targetAudience,
        CancellationToken cancellationToken = default)
    {
        // Core templates for value props
        var templates = new[]
        {
            "{Product} helps {audience} {benefit} by {how}.",
            "{Product} is the {category} that {benefit} for {audience}.",
            "For {audience} who need {need}, {Product} {benefit}.",
            "{Product}: {benefit} for {audience}. No {pain_point}."
        };

        var suggestions = new List<string>();

        // Generate suggestions based on capabilities
        var primaryBenefit = InferPrimaryBenefit(keyCapabilities);
        var pain = InferPainPoint(keyCapabilities);

        foreach (var template in templates)
        {
            var suggestion = template
                .Replace("{Product}", "Control Room")
                .Replace("{audience}", targetAudience)
                .Replace("{benefit}", primaryBenefit)
                .Replace("{how}", keyCapabilities.FirstOrDefault() ?? "automation")
                .Replace("{category}", "DevOps control plane")
                .Replace("{need}", InferNeed(keyCapabilities))
                .Replace("{pain_point}", pain);

            suggestions.Add(suggestion);
        }

        return new ValuePropositionSuggestion
        {
            Suggestions = suggestions,
            Recommendations = new List<string>
            {
                "Keep it under 15 words for maximum impact",
                "Focus on the outcome, not the features",
                "Make the benefit specific and measurable if possible",
                "Address the primary pain point directly"
            }
        };
    }

    // ========================================================================
    // IDEAL CUSTOMER PROFILE: Clear ICP
    // ========================================================================

    /// <summary>
    /// Gets the current Ideal Customer Profile.
    /// </summary>
    public async Task<IdealCustomerProfile> GetIdealCustomerProfileAsync(
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetIdealCustomerProfileAsync(cancellationToken)
            ?? GetDefaultICP();
    }

    /// <summary>
    /// Updates the Ideal Customer Profile.
    /// </summary>
    public async Task<ICPResult> UpdateIdealCustomerProfileAsync(
        IdealCustomerProfile icp,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateICP(icp);
        if (!validation.IsValid)
        {
            return new ICPResult
            {
                Success = false,
                Errors = validation.Errors
            };
        }

        icp.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveIdealCustomerProfileAsync(icp, cancellationToken);

        NarrativeUpdated?.Invoke(this, new NarrativeUpdatedEventArgs("ICP"));

        return new ICPResult
        {
            Success = true,
            Profile = icp
        };
    }

    /// <summary>
    /// Scores a potential customer against the ICP.
    /// </summary>
    public async Task<ICPMatchResult> ScoreCustomerMatchAsync(
        CustomerProfile customer,
        CancellationToken cancellationToken = default)
    {
        var icp = await GetIdealCustomerProfileAsync(cancellationToken);

        var scores = new Dictionary<string, double>();
        var matches = new List<string>();
        var gaps = new List<string>();

        // Score each dimension
        // Company size
        var sizeScore = ScoreCompanySize(customer.CompanySize, icp.CompanySize);
        scores["companySize"] = sizeScore;
        if (sizeScore > 0.7) matches.Add("Company size aligns with ICP");
        else gaps.Add($"Company size ({customer.CompanySize}) differs from ideal ({icp.CompanySize})");

        // Industry
        var industryScore = icp.Industries.Contains(customer.Industry) ? 1.0 : 0.3;
        scores["industry"] = industryScore;
        if (industryScore > 0.7) matches.Add("Industry match");
        else gaps.Add($"Industry ({customer.Industry}) not in target list");

        // Tech stack
        var techScore = ScoreTechStackMatch(customer.TechStack, icp.PreferredTechStack);
        scores["techStack"] = techScore;
        if (techScore > 0.5) matches.Add("Technology stack compatibility");
        else gaps.Add("Limited tech stack overlap");

        // Pain points
        var painScore = ScorePainPointMatch(customer.PainPoints, icp.KeyPainPoints);
        scores["painPoints"] = painScore;
        if (painScore > 0.6) matches.Add("Key pain points align");
        else gaps.Add("Pain points don't strongly match our value prop");

        // Budget
        var budgetScore = customer.Budget >= icp.MinimumBudget ? 1.0 : (double)customer.Budget / (double)icp.MinimumBudget;
        scores["budget"] = budgetScore;
        if (budgetScore >= 1.0) matches.Add("Budget meets minimum threshold");
        else gaps.Add($"Budget ({customer.Budget:C}) below minimum ({icp.MinimumBudget:C})");

        // Calculate overall score
        var weights = new Dictionary<string, double>
        {
            ["companySize"] = 0.15,
            ["industry"] = 0.10,
            ["techStack"] = 0.20,
            ["painPoints"] = 0.35,
            ["budget"] = 0.20
        };

        var overallScore = scores.Sum(kvp => kvp.Value * weights.GetValueOrDefault(kvp.Key, 0.1));

        return new ICPMatchResult
        {
            OverallScore = overallScore,
            CategoryScores = scores,
            Matches = matches,
            Gaps = gaps,
            Recommendation = overallScore switch
            {
                >= 0.8 => "Excellent fit - prioritize this opportunity",
                >= 0.6 => "Good fit - proceed with tailored approach",
                >= 0.4 => "Moderate fit - validate pain points before investing heavily",
                _ => "Poor fit - likely not ideal customer, consider qualifying out"
            }
        };
    }

    // ========================================================================
    // DEMO EXPERIENCES: Logical Story Flow
    // ========================================================================

    /// <summary>
    /// Gets available demo scenarios.
    /// </summary>
    public async Task<IReadOnlyList<DemoScenario>> GetDemoScenariosAsync(
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetDemoScenariosAsync(cancellationToken);
    }

    /// <summary>
    /// Creates a new demo scenario with story flow.
    /// </summary>
    public async Task<DemoScenarioResult> CreateDemoScenarioAsync(
        DemoScenario scenario,
        CancellationToken cancellationToken = default)
    {
        // Validate story flow
        var validation = ValidateDemoStoryFlow(scenario);
        if (!validation.IsValid)
        {
            return new DemoScenarioResult
            {
                Success = false,
                Errors = validation.Errors
            };
        }

        scenario.Id = Guid.NewGuid().ToString("N");
        scenario.CreatedAt = DateTimeOffset.UtcNow;

        await _repository.SaveDemoScenarioAsync(scenario, cancellationToken);

        return new DemoScenarioResult
        {
            Success = true,
            Scenario = scenario
        };
    }

    /// <summary>
    /// Generates a demo script based on scenario and audience.
    /// </summary>
    public async Task<DemoScript> GenerateDemoScriptAsync(
        string scenarioId,
        DemoAudience audience,
        CancellationToken cancellationToken = default)
    {
        var scenario = await _repository.GetDemoScenarioAsync(scenarioId, cancellationToken);
        if (scenario == null)
        {
            return new DemoScript
            {
                ScenarioId = scenarioId,
                Error = "Scenario not found"
            };
        }

        var valueProp = await GetValuePropositionAsync(cancellationToken);

        // Build script following story arc
        var sections = new List<DemoSection>();

        // 1. Opening Hook (30 seconds)
        sections.Add(new DemoSection
        {
            Order = 1,
            Title = "The Hook",
            Duration = TimeSpan.FromSeconds(30),
            Purpose = "Capture attention with relevant pain point",
            TalkingPoints = new List<string>
            {
                $"Have you ever experienced {scenario.PrimaryPainPoint}?",
                $"Most {audience.Role}s tell us they spend {scenario.TimeWastedEstimate} dealing with this.",
                "Today I'll show you how Control Room eliminates this problem."
            },
            AvoidSaying = new List<string>
            {
                "Let me show you all our features",
                "We have the most comprehensive platform"
            }
        });

        // 2. Context Setting (1 minute)
        sections.Add(new DemoSection
        {
            Order = 2,
            Title = "Set the Scene",
            Duration = TimeSpan.FromMinutes(1),
            Purpose = "Establish relatable scenario",
            TalkingPoints = new List<string>
            {
                $"Imagine you're a {audience.Role} at a company like yours.",
                $"It's {scenario.ContextScenario}.",
                "Here's what that looks like in Control Room."
            },
            ShowFeatures = scenario.SetupFeatures
        });

        // 3. Problem Demonstration (2 minutes)
        sections.Add(new DemoSection
        {
            Order = 3,
            Title = "Feel the Pain",
            Duration = TimeSpan.FromMinutes(2),
            Purpose = "Show the problem clearly before the solution",
            TalkingPoints = new List<string>
            {
                "Without proper tooling, here's what happens...",
                $"You'd have to {scenario.ManualProcess}.",
                "This is error-prone and time-consuming."
            },
            ShowFeatures = new List<string>()
        });

        // 4. Solution Reveal (3 minutes)
        sections.Add(new DemoSection
        {
            Order = 4,
            Title = "The Transformation",
            Duration = TimeSpan.FromMinutes(3),
            Purpose = "Show the solution in action",
            TalkingPoints = new List<string>
            {
                "Now let me show you how Control Room handles this.",
                "With one click...",
                $"What used to take {scenario.TimeWastedEstimate} now takes seconds."
            },
            ShowFeatures = scenario.CoreFeatures,
            AvoidSaying = new List<string>
            {
                "We also have...",
                "Another feature is...",
                "And we can also..."
            }
        });

        // 5. Value Summary (1 minute)
        sections.Add(new DemoSection
        {
            Order = 5,
            Title = "Connect to Value",
            Duration = TimeSpan.FromMinutes(1),
            Purpose = "Reinforce benefits and next steps",
            TalkingPoints = new List<string>
            {
                valueProp.OneSentence,
                $"For your team, this means {CalculatePersonalizedBenefit(audience, scenario)}.",
                "What questions do you have about how this would work for you?"
            }
        });

        return new DemoScript
        {
            ScenarioId = scenarioId,
            Audience = audience,
            TotalDuration = TimeSpan.FromMinutes(7.5),
            Sections = sections,
            GeneralGuidelines = new List<string>
            {
                "Let the audience drive - pause for questions",
                "Focus on outcomes, not features",
                "Use their terminology, not ours",
                "If they light up on something, go deeper there",
                "Don't show features they didn't ask about"
            },
            AntiPatterns = new List<string>
            {
                "Feature dumping - showing everything possible",
                "Death by dropdown - navigating every menu",
                "Reading the screen - let visuals speak",
                "Skipping the pain - jumping straight to solution"
            }
        };
    }

    /// <summary>
    /// Starts a guided demo session.
    /// </summary>
    public async Task<DemoSessionResult> StartDemoSessionAsync(
        string scriptId,
        CancellationToken cancellationToken = default)
    {
        var session = await _demoOrchestrator.StartSessionAsync(scriptId, cancellationToken);

        return new DemoSessionResult
        {
            SessionId = session.Id,
            Started = true,
            CurrentSection = session.CurrentSection,
            TotalSections = session.TotalSections
        };
    }

    /// <summary>
    /// Advances to the next section of a demo.
    /// </summary>
    public async Task<DemoAdvanceResult> AdvanceDemoAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var result = await _demoOrchestrator.AdvanceAsync(sessionId, cancellationToken);

        if (result.IsComplete)
        {
            DemoCompleted?.Invoke(this, new DemoCompletedEventArgs(sessionId));
        }

        return result;
    }

    /// <summary>
    /// Records feedback from a demo session.
    /// </summary>
    public async Task RecordDemoFeedbackAsync(
        string sessionId,
        DemoFeedback feedback,
        CancellationToken cancellationToken = default)
    {
        feedback.SessionId = sessionId;
        feedback.RecordedAt = DateTimeOffset.UtcNow;

        await _repository.SaveDemoFeedbackAsync(feedback, cancellationToken);
    }

    /// <summary>
    /// Gets demo analytics.
    /// </summary>
    public async Task<DemoAnalytics> GetDemoAnalyticsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var feedbacks = await _repository.GetDemoFeedbacksAsync(from, to, cancellationToken);

        var byScenario = feedbacks
            .GroupBy(f => f.ScenarioId)
            .Select(g => new ScenarioDemoStats
            {
                ScenarioId = g.Key,
                DemoCount = g.Count(),
                AverageEngagement = g.Average(f => f.EngagementScore),
                ConversionRate = (double)g.Count(f => f.ResultedInNextStep) / g.Count(),
                TopInterestAreas = g.SelectMany(f => f.HighInterestFeatures)
                    .GroupBy(f => f)
                    .OrderByDescending(fg => fg.Count())
                    .Take(3)
                    .Select(fg => fg.Key)
                    .ToList()
            })
            .ToList();

        var commonObjections = feedbacks
            .SelectMany(f => f.Objections)
            .GroupBy(o => o)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new ObjectionFrequency
            {
                Objection = g.Key,
                Count = g.Count()
            })
            .ToList();

        return new DemoAnalytics
        {
            Period = (from, to),
            TotalDemos = feedbacks.Count,
            AverageEngagement = feedbacks.Any() ? feedbacks.Average(f => f.EngagementScore) : 0,
            OverallConversionRate = feedbacks.Any()
                ? (double)feedbacks.Count(f => f.ResultedInNextStep) / feedbacks.Count
                : 0,
            ByScenario = byScenario,
            CommonObjections = commonObjections,
            Recommendations = GenerateDemoRecommendations(byScenario, commonObjections)
        };
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    private ValueProposition GetDefaultValueProposition()
    {
        return new ValueProposition
        {
            OneSentence = "Control Room gives DevOps teams a unified control plane to manage cloud operations safely and efficiently.",
            ForWhom = "DevOps teams and platform engineers",
            PrimaryBenefit = "Unified visibility and control across cloud providers",
            HowItWorks = "Aggregates metrics, automates runbooks, and enforces safety guardrails",
            Differentiator = "Built for teams who need enterprise reliability without enterprise complexity"
        };
    }

    private IdealCustomerProfile GetDefaultICP()
    {
        return new IdealCustomerProfile
        {
            CompanySize = CompanySize.MidMarket,
            Industries = new List<string> { "SaaS", "FinTech", "E-Commerce", "Healthcare Tech" },
            PreferredTechStack = new List<string> { "AWS", "Azure", "Kubernetes", "Terraform" },
            KeyPainPoints = new List<string>
            {
                "Managing multiple cloud providers",
                "Incident response across distributed systems",
                "Audit compliance for cloud operations"
            },
            MinimumBudget = 10000,
            DecisionMakers = new List<string> { "VP Engineering", "Platform Team Lead", "CTO" }
        };
    }

    private NarrativeValidationResult ValidateValueProposition(ValueProposition valueProp)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(valueProp.OneSentence))
            errors.Add("One-sentence value prop is required");
        else if (valueProp.OneSentence.Split(' ').Length > 25)
            errors.Add("One-sentence value prop should be under 25 words");

        if (string.IsNullOrWhiteSpace(valueProp.ForWhom))
            errors.Add("Target audience (ForWhom) is required");

        if (string.IsNullOrWhiteSpace(valueProp.PrimaryBenefit))
            errors.Add("Primary benefit is required");

        return new NarrativeValidationResult
        {
            IsValid = !errors.Any(),
            Errors = errors
        };
    }

    private NarrativeValidationResult ValidateICP(IdealCustomerProfile icp)
    {
        var errors = new List<string>();

        if (!icp.Industries.Any())
            errors.Add("At least one target industry is required");

        if (!icp.KeyPainPoints.Any())
            errors.Add("At least one key pain point is required");

        if (!icp.DecisionMakers.Any())
            errors.Add("At least one decision maker role is required");

        return new NarrativeValidationResult
        {
            IsValid = !errors.Any(),
            Errors = errors
        };
    }

    private NarrativeValidationResult ValidateDemoStoryFlow(DemoScenario scenario)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(scenario.Name))
            errors.Add("Scenario name is required");

        if (string.IsNullOrWhiteSpace(scenario.PrimaryPainPoint))
            errors.Add("Primary pain point must be defined for story flow");

        if (!scenario.CoreFeatures.Any())
            errors.Add("At least one core feature to demonstrate is required");

        if (scenario.CoreFeatures.Count > 3)
            errors.Add("Limit core features to 3 to avoid feature dumping");

        return new NarrativeValidationResult
        {
            IsValid = !errors.Any(),
            Errors = errors
        };
    }

    private string InferPrimaryBenefit(IReadOnlyList<string> capabilities)
    {
        if (capabilities.Any(c => c.Contains("automat", StringComparison.OrdinalIgnoreCase)))
            return "automate repetitive tasks";
        if (capabilities.Any(c => c.Contains("monitor", StringComparison.OrdinalIgnoreCase)))
            return "get visibility across your infrastructure";
        if (capabilities.Any(c => c.Contains("secur", StringComparison.OrdinalIgnoreCase)))
            return "secure operations with built-in guardrails";

        return "streamline cloud operations";
    }

    private string InferPainPoint(IReadOnlyList<string> capabilities)
    {
        if (capabilities.Any(c => c.Contains("automat", StringComparison.OrdinalIgnoreCase)))
            return "manual toil";
        if (capabilities.Any(c => c.Contains("monitor", StringComparison.OrdinalIgnoreCase)))
            return "blind spots";

        return "complexity";
    }

    private string InferNeed(IReadOnlyList<string> capabilities)
    {
        if (capabilities.Any(c => c.Contains("multi-cloud", StringComparison.OrdinalIgnoreCase)))
            return "to manage multiple cloud providers";

        return "to operate efficiently at scale";
    }

    private double ScoreCompanySize(CompanySize actual, CompanySize ideal)
    {
        var distance = Math.Abs((int)actual - (int)ideal);
        return distance switch
        {
            0 => 1.0,
            1 => 0.7,
            2 => 0.4,
            _ => 0.2
        };
    }

    private double ScoreTechStackMatch(IReadOnlyList<string> actual, IReadOnlyList<string> preferred)
    {
        if (!preferred.Any()) return 0.5;

        var matches = actual.Intersect(preferred, StringComparer.OrdinalIgnoreCase).Count();
        return (double)matches / preferred.Count;
    }

    private double ScorePainPointMatch(IReadOnlyList<string> actual, IReadOnlyList<string> target)
    {
        if (!target.Any()) return 0.5;

        var matches = 0;
        foreach (var actualPain in actual)
        {
            if (target.Any(t => actualPain.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                               t.Contains(actualPain, StringComparison.OrdinalIgnoreCase)))
            {
                matches++;
            }
        }

        return Math.Min(1.0, (double)matches / target.Count);
    }

    private string CalculatePersonalizedBenefit(DemoAudience audience, DemoScenario scenario)
    {
        return audience.Role switch
        {
            "Engineering Manager" => $"your team gets back {scenario.TimeWastedEstimate} each week",
            "DevOps Engineer" => "fewer late-night pages and faster incident resolution",
            "CTO" => "reduced operational risk and better compliance posture",
            "VP Engineering" => "improved team velocity and reduced burnout",
            _ => "operational efficiency and peace of mind"
        };
    }

    private List<string> GenerateDemoRecommendations(
        List<ScenarioDemoStats> byScenario,
        List<ObjectionFrequency> objections)
    {
        var recommendations = new List<string>();

        var bestScenario = byScenario.OrderByDescending(s => s.ConversionRate).FirstOrDefault();
        if (bestScenario != null)
        {
            recommendations.Add($"Focus on '{bestScenario.ScenarioId}' scenario - highest conversion at {bestScenario.ConversionRate:P0}");
        }

        var topObjection = objections.FirstOrDefault();
        if (topObjection != null)
        {
            recommendations.Add($"Prepare stronger response for common objection: '{topObjection.Objection}'");
        }

        var lowEngagement = byScenario.Where(s => s.AverageEngagement < 3).ToList();
        if (lowEngagement.Any())
        {
            recommendations.Add($"Review low-engagement scenarios: {string.Join(", ", lowEngagement.Select(s => s.ScenarioId))}");
        }

        return recommendations;
    }
}

// ========================================================================
// SUPPORTING TYPES
// ========================================================================

public class ValueProposition
{
    public required string OneSentence { get; init; }
    public required string ForWhom { get; init; }
    public required string PrimaryBenefit { get; init; }
    public required string HowItWorks { get; init; }
    public string? Differentiator { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ValuePropositionResult
{
    public bool Success { get; init; }
    public ValueProposition? ValueProposition { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public class ValuePropositionSuggestion
{
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();
}

public class IdealCustomerProfile
{
    public CompanySize CompanySize { get; init; }
    public IReadOnlyList<string> Industries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreferredTechStack { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KeyPainPoints { get; init; } = Array.Empty<string>();
    public decimal MinimumBudget { get; init; }
    public IReadOnlyList<string> DecisionMakers { get; init; } = Array.Empty<string>();
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CompanySize
{
    Startup,      // < 50 employees
    SmallBusiness,// 50-200 employees
    MidMarket,    // 200-1000 employees
    Enterprise,   // 1000-10000 employees
    LargeEnterprise // > 10000 employees
}

public class ICPResult
{
    public bool Success { get; init; }
    public IdealCustomerProfile? Profile { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public class CustomerProfile
{
    public required string CompanyName { get; init; }
    public CompanySize CompanySize { get; init; }
    public required string Industry { get; init; }
    public IReadOnlyList<string> TechStack { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PainPoints { get; init; } = Array.Empty<string>();
    public decimal Budget { get; init; }
}

public class ICPMatchResult
{
    public double OverallScore { get; init; }
    public Dictionary<string, double> CategoryScores { get; init; } = new();
    public IReadOnlyList<string> Matches { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();
    public required string Recommendation { get; init; }
}

public class DemoScenario
{
    public string Id { get; set; } = string.Empty;
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string PrimaryPainPoint { get; init; }
    public required string ContextScenario { get; init; }
    public required string ManualProcess { get; init; }
    public required string TimeWastedEstimate { get; init; }
    public IReadOnlyList<string> SetupFeatures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CoreFeatures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TargetAudiences { get; init; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; set; }
}

public class DemoScenarioResult
{
    public bool Success { get; init; }
    public DemoScenario? Scenario { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public class DemoAudience
{
    public required string Role { get; init; }
    public required string Industry { get; init; }
    public IReadOnlyList<string> KnownPainPoints { get; init; } = Array.Empty<string>();
    public int TechnicalLevel { get; init; } // 1-5
}

public class DemoScript
{
    public required string ScenarioId { get; init; }
    public DemoAudience? Audience { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public IReadOnlyList<DemoSection> Sections { get; init; } = Array.Empty<DemoSection>();
    public IReadOnlyList<string> GeneralGuidelines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AntiPatterns { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

public class DemoSection
{
    public int Order { get; init; }
    public required string Title { get; init; }
    public TimeSpan Duration { get; init; }
    public required string Purpose { get; init; }
    public IReadOnlyList<string> TalkingPoints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShowFeatures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AvoidSaying { get; init; } = Array.Empty<string>();
}

public class DemoSessionResult
{
    public required string SessionId { get; init; }
    public bool Started { get; init; }
    public int CurrentSection { get; init; }
    public int TotalSections { get; init; }
}

public class DemoAdvanceResult
{
    public bool IsComplete { get; init; }
    public int CurrentSection { get; init; }
    public DemoSection? NextSection { get; init; }
}

public class DemoSession
{
    public required string Id { get; init; }
    public int CurrentSection { get; init; }
    public int TotalSections { get; init; }
}

public class DemoFeedback
{
    public string SessionId { get; set; } = string.Empty;
    public string ScenarioId { get; init; } = string.Empty;
    public double EngagementScore { get; init; } // 1-5
    public bool ResultedInNextStep { get; init; }
    public IReadOnlyList<string> HighInterestFeatures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Objections { get; init; } = Array.Empty<string>();
    public string? Notes { get; init; }
    public DateTimeOffset RecordedAt { get; set; }
}

public class DemoAnalytics
{
    public (DateTimeOffset From, DateTimeOffset To) Period { get; init; }
    public int TotalDemos { get; init; }
    public double AverageEngagement { get; init; }
    public double OverallConversionRate { get; init; }
    public IReadOnlyList<ScenarioDemoStats> ByScenario { get; init; } = Array.Empty<ScenarioDemoStats>();
    public IReadOnlyList<ObjectionFrequency> CommonObjections { get; init; } = Array.Empty<ObjectionFrequency>();
    public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();
}

public class ScenarioDemoStats
{
    public required string ScenarioId { get; init; }
    public int DemoCount { get; init; }
    public double AverageEngagement { get; init; }
    public double ConversionRate { get; init; }
    public IReadOnlyList<string> TopInterestAreas { get; init; } = Array.Empty<string>();
}

public class ObjectionFrequency
{
    public required string Objection { get; init; }
    public int Count { get; init; }
}

public class NarrativeValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public class NarrativeUpdatedEventArgs : EventArgs
{
    public string NarrativeType { get; }
    public NarrativeUpdatedEventArgs(string narrativeType) => NarrativeType = narrativeType;
}

public class DemoCompletedEventArgs : EventArgs
{
    public string SessionId { get; }
    public DemoCompletedEventArgs(string sessionId) => SessionId = sessionId;
}

// ========================================================================
// REPOSITORY INTERFACES
// ========================================================================

public interface IProductNarrativeRepository
{
    Task<ValueProposition?> GetValuePropositionAsync(CancellationToken cancellationToken);
    Task SaveValuePropositionAsync(ValueProposition valueProp, CancellationToken cancellationToken);
    Task<IdealCustomerProfile?> GetIdealCustomerProfileAsync(CancellationToken cancellationToken);
    Task SaveIdealCustomerProfileAsync(IdealCustomerProfile icp, CancellationToken cancellationToken);
    Task<IReadOnlyList<DemoScenario>> GetDemoScenariosAsync(CancellationToken cancellationToken);
    Task<DemoScenario?> GetDemoScenarioAsync(string id, CancellationToken cancellationToken);
    Task SaveDemoScenarioAsync(DemoScenario scenario, CancellationToken cancellationToken);
    Task SaveDemoFeedbackAsync(DemoFeedback feedback, CancellationToken cancellationToken);
    Task<IReadOnlyList<DemoFeedback>> GetDemoFeedbacksAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public interface IDemoOrchestrator
{
    Task<DemoSession> StartSessionAsync(string scriptId, CancellationToken cancellationToken);
    Task<DemoAdvanceResult> AdvanceAsync(string sessionId, CancellationToken cancellationToken);
}
