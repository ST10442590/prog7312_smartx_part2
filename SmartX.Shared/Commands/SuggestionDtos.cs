namespace SmartX.Shared.Commands;

// What kind of thing the operator did. The engine learns transitions
// between these, so the kind is part of the event's identity.
public enum ActivityKind
{
    Search = 0,
    Command = 1,
    Alert = 2,
    DeviceInspect = 3
}

// One thing the operator did, or one thing that happened to them.
public sealed class ActivityEvent
{
    public ActivityKind Kind { get; set; }

    // What the event was about: a device id, or a search term.
    public string Subject { get; set; } = string.Empty;

    // Extra detail — the command kind, or the alert type.
    public string? Detail { get; set; }

    public DateTimeOffset OccurredUtc { get; set; } = DateTimeOffset.UtcNow;

    // The key the transition table is built on. Two events with the same
    // key are treated as the same situation recurring.
    public string Key => Detail is null
        ? $"{Kind}:{Subject}"
        : $"{Kind}:{Subject}:{Detail}";

    public string Describe() => Kind switch
    {
        ActivityKind.Search => $"searched \"{Subject}\"",
        ActivityKind.Command => $"sent {Detail} to {Subject}",
        ActivityKind.Alert => $"{Detail} on {Subject}",
        _ => $"inspected {Subject}"
    };
}

// Why a suggestion was made. Shown in the UI so the recommendation is
// explicable rather than magic.
public enum SuggestionBasis
{
    // Learned from what the operator did after this exact situation before.
    LearnedTransition = 0,

    // The device is degrading but has not been looked at.
    UnattendedAnomaly = 1,

    // This device is frequently acted on after the current one.
    FrequentPairing = 2
}

public sealed class SuggestedActionDto
{
    public string Title { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public SuggestionBasis Basis { get; set; }

    // Plain-language justification, e.g. "You searched ESP32-J09 after this
    // alert 4 of the last 5 times."
    public string Rationale { get; set; } = string.Empty;

    // 0 to 1. Transition probability for learned suggestions, severity for
    // unattended ones.
    public double Confidence { get; set; }

    // How many times the pattern was observed. One observation is a
    // coincidence, so this is shown alongside the confidence.
    public int Observations { get; set; }

    // The command to run, when the suggestion is actionable directly.
    public CommandKind? SuggestedCommand { get; set; }
}

// One learned transition, exposed so the model can be inspected rather than
// taken on trust.
public sealed class TransitionDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Probability { get; set; }
}

public sealed class SuggestionReportDto
{
    public List<SuggestedActionDto> Suggestions { get; set; } = [];

    // Recent operator activity, newest first.
    public List<ActivityEvent> RecentActivity { get; set; } = [];

    // The strongest learned transitions, for the model inspector.
    public List<TransitionDto> TopTransitions { get; set; } = [];

    public int EventsObserved { get; set; }
    public int DistinctSituations { get; set; }
    public int TransitionsLearned { get; set; }
}