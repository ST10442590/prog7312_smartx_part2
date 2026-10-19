using SmartX.Shared.Commands;
using SmartX.Shared.Sensors;

namespace SmartX.Api.Services;

// Learns what the operator does, and suggests the next action before they
// go looking for it.
//
// The model is a transition table: for each situation the operator has been
// in, a count of what they did next.
//
//     Dictionary<string, Dictionary<string, int>>
//        situation  ->   next action  ->  times observed
//
// This is a first-order Markov model. It is deliberately simple, and that
// simplicity is the point: every suggestion can be explained back to the
// operator as "you did this N times out of M", which a neural approach
// could not offer. An operator who cannot see why a system is recommending
// something will not trust it.
public sealed class SuggestionEngine
{
    private readonly DeviceRegistry _registry;
    private readonly CommandStreamService _stream;
    private readonly ILogger<SuggestionEngine> _logger;
    private readonly Lock _gate = new();

    // The learned model. Outer key is the situation; inner key is what
    // followed it, with a count.
    private readonly Dictionary<string, Dictionary<string, int>> _transitions =
        new(StringComparer.OrdinalIgnoreCase);

    // How often each situation has been followed by an operator action, so
    // counts convert to probabilities.
    private readonly Dictionary<string, int> _situationCounts =
        new(StringComparer.OrdinalIgnoreCase);

    // Rolling activity log, newest first.
    private readonly List<ActivityEvent> _activity = [];
    private const int MaxActivity = 60;

    // The previous event of any kind, used to close transitions.
    private ActivityEvent? _previous;

    // The operator's last deliberate action, tracked separately.
    //
    // Predicting from _previous alone does not work: the mesh raises alerts
    // several times a second, so the most recent event is almost never the
    // thing the operator just did. The operator's context is what they
    // last chose to do, not what last happened in the world.
    private ActivityEvent? _lastOperatorAction;

    // Devices the operator has deliberately looked at. An anomaly on a
    // device already being investigated is not worth suggesting.
    private readonly HashSet<string> _recentlyAttended =
        new(StringComparer.OrdinalIgnoreCase);

    // Only pairs seen at least this many times are suggested. One
    // observation is a coincidence, not a pattern.
    private const int MinObservations = 2;

    // Transitions only count if the second event followed reasonably soon.
    // Something done ten minutes later is a separate task, not a response.
    private static readonly TimeSpan TransitionWindow = TimeSpan.FromMinutes(3);

    // How far back an event can be and still count as the operator's
    // current context.
    private static readonly TimeSpan ContextWindow = TimeSpan.FromMinutes(5);

    // How many recent alerts are treated as live context the operator may
    // be about to respond to.
    private const int AlertContextDepth = 4;

    private int _eventsObserved;

    public SuggestionEngine(
        DeviceRegistry registry,
        CommandStreamService stream,
        ILogger<SuggestionEngine> logger)
    {
        _registry = registry;
        _stream = stream;
        _logger = logger;
    }

    // True for things the operator chose to do, as opposed to things that
    // merely happened to them.
    private static bool IsOperatorAction(ActivityKind kind) =>
        kind is ActivityKind.Search or ActivityKind.Command or ActivityKind.DeviceInspect;

    // Records an event and learns the transition from whatever came before.
    public void Record(ActivityEvent current)
    {
        lock (_gate)
        {
            _eventsObserved++;

            _activity.Insert(0, current);
            if (_activity.Count > MaxActivity)
            {
                _activity.RemoveRange(MaxActivity, _activity.Count - MaxActivity);
            }

            if (IsOperatorAction(current.Kind))
            {
                // Only an operator action counts as attending to a device.
                // An alert firing is the opposite of attention — it is the
                // thing that needs attention — so alerts must not mark
                // themselves handled, or no anomaly could be suggested.
                if (!string.IsNullOrWhiteSpace(current.Subject))
                {
                    _recentlyAttended.Add(current.Subject);
                }

                // A transition is only worth learning when the thing that
                // followed was an operator action. Two unrelated alerts
                // firing in the same window is coincidence, not behaviour.
                if (_previous is not null &&
                    current.OccurredUtc - _previous.OccurredUtc <= TransitionWindow &&
                    !string.Equals(_previous.Key, current.Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Learn(_previous.Key, current.Key);
                }

                _lastOperatorAction = current;
            }

            _previous = current;
        }
    }

    // Increments the count for one situation-to-action pair.
    private void Learn(string from, string to)
    {
        if (!_transitions.TryGetValue(from, out var followers))
        {
            followers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _transitions[from] = followers;
        }

        followers.TryGetValue(to, out var count);
        followers[to] = count + 1;

        _situationCounts.TryGetValue(from, out var total);
        _situationCounts[from] = total + 1;

        _logger.LogDebug("Learned transition {From} -> {To} (now {Count})",
            from, to, followers[to]);
    }

    // Builds the current recommendations.
    public SuggestionReportDto Report(int take = 5)
    {
        lock (_gate)
        {
            var suggestions = new List<SuggestedActionDto>();

            suggestions.AddRange(FromLearnedTransitions());
            suggestions.AddRange(FromUnattendedAnomalies());

            // Highest confidence first, and de-duplicated by device so one
            // problem device does not fill the whole panel.
            var ranked = suggestions
                .GroupBy(s => s.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.Confidence).First())
                .OrderByDescending(s => s.Confidence)
                .ThenByDescending(s => s.Observations)
                .Take(take)
                .ToList();

            return new SuggestionReportDto
            {
                Suggestions = ranked,
                RecentActivity = [.. _activity.Take(12)],
                TopTransitions = TopTransitionsLocked(8),
                EventsObserved = _eventsObserved,
                DistinctSituations = _transitions.Count,
                TransitionsLearned = _transitions.Values.Sum(f => f.Count)
            };
        }
    }

    // The situations the operator could plausibly be responding to right
    // now: their last deliberate action, plus the handful of most recent
    // distinct alerts.
    private List<ActivityEvent> CurrentContextLocked()
    {
        var now = DateTimeOffset.UtcNow;
        var context = new List<ActivityEvent>();

        if (_lastOperatorAction is not null &&
            now - _lastOperatorAction.OccurredUtc <= ContextWindow)
        {
            context.Add(_lastOperatorAction);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in _activity)
        {
            if (evt.Kind != ActivityKind.Alert) continue;
            if (now - evt.OccurredUtc > ContextWindow) break;
            if (!seen.Add(evt.Key)) continue;

            context.Add(evt);
            if (seen.Count >= AlertContextDepth) break;
        }

        return context;
    }

    // For each situation the operator is currently in, suggests whatever
    // they usually do next.
    private List<SuggestedActionDto> FromLearnedTransitions()
    {
        var results = new List<SuggestedActionDto>();

        foreach (var situation in CurrentContextLocked())
        {
            if (!_transitions.TryGetValue(situation.Key, out var followers)) continue;
            if (!_situationCounts.TryGetValue(situation.Key, out var total) || total == 0)
                continue;

            foreach (var (next, count) in followers)
            {
                if (count < MinObservations) continue;

                var probability = (double)count / total;
                var deviceId = SubjectOf(next);

                // Do not suggest the thing they just did.
                if (string.Equals(next, situation.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var profile = deviceId is null ? null : _registry.GetProfile(deviceId);

                results.Add(new SuggestedActionDto
                {
                    Title = TitleFor(next, profile),
                    DeviceId = profile?.Id ?? deviceId ?? string.Empty,
                    DeviceName = profile?.DisplayName ?? deviceId ?? "unknown device",
                    Basis = SuggestionBasis.LearnedTransition,
                    Confidence = Math.Round(probability, 2),
                    Observations = count,
                    Rationale =
                        $"After {situation.Describe()}, you {VerbFor(next)} " +
                        $"{count} of the last {total} times.",
                    SuggestedCommand = CommandOf(next)
                });
            }
        }

        return results;
    }

    // Devices that are degrading but have not been looked at. This is the
    // proactive half: it surfaces a problem before the operator searches
    // for it, which is what the brief asks for.
    private List<SuggestedActionDto> FromUnattendedAnomalies()
    {
        var results = new List<SuggestedActionDto>();
        var now = DateTimeOffset.UtcNow;

        foreach (var tile in _registry.Snapshot(now))
        {
            if (tile.State is HealthState.Nominal) continue;

            // Already being dealt with, so suggesting it would be noise.
            if (_recentlyAttended.Contains(tile.DeviceId)) continue;

            var confidence = tile.State switch
            {
                HealthState.Disconnected => 0.95,
                HealthState.Spike => 0.85,
                _ => 0.55
            };

            var reason = tile.State switch
            {
                HealthState.Disconnected =>
                    $"Silent for {tile.SilenceSeconds:0} seconds and not yet investigated.",
                HealthState.Spike =>
                    $"Reading {Math.Abs(tile.ZScore):0.0} deviations from its baseline.",
                _ =>
                    $"Drifting {Math.Abs(tile.ZScore):0.0} deviations from its baseline."
            };

            results.Add(new SuggestedActionDto
            {
                Title = $"Inspect {tile.DisplayName}",
                DeviceId = tile.DeviceId,
                DeviceName = tile.DisplayName,
                Basis = SuggestionBasis.UnattendedAnomaly,
                Confidence = confidence,
                Observations = 1,
                Rationale = reason,
                SuggestedCommand = tile.State == HealthState.Disconnected
                    ? CommandKind.ResetNode
                    : null
            });
        }

        return results;
    }

    private List<TransitionDto> TopTransitionsLocked(int take)
    {
        var rows = new List<TransitionDto>();

        foreach (var (from, followers) in _transitions)
        {
            _situationCounts.TryGetValue(from, out var total);
            if (total == 0) continue;

            foreach (var (to, count) in followers)
            {
                rows.Add(new TransitionDto
                {
                    From = from,
                    To = to,
                    Count = count,
                    Probability = Math.Round((double)count / total, 2)
                });
            }
        }

        return rows
            .OrderByDescending(r => r.Count)
            .ThenByDescending(r => r.Probability)
            .Take(take)
            .ToList();
    }

    // Lets the operator dismiss a device so it stops being suggested.
    public void MarkAttended(string deviceId)
    {
        lock (_gate) _recentlyAttended.Add(deviceId);
    }

    // Clears the attention set, so unresolved problems resurface. Called on
    // a timer rather than never, because a device glanced at five minutes
    // ago and still broken deserves raising again.
    public void ExpireAttention()
    {
        lock (_gate) _recentlyAttended.Clear();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _transitions.Clear();
            _situationCounts.Clear();
            _activity.Clear();
            _recentlyAttended.Clear();
            _previous = null;
            _lastOperatorAction = null;
            _eventsObserved = 0;
        }
    }

    // ---------------------------------------------------------- helpers

    // Pulls the device id out of an event key like "Search:ESP32-A00".
    private static string? SubjectOf(string key)
    {
        var parts = key.Split(':');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static CommandKind? CommandOf(string key)
    {
        var parts = key.Split(':');
        if (parts.Length < 3) return null;
        return Enum.TryParse<CommandKind>(parts[2], out var kind) ? kind : null;
    }

    private static string VerbFor(string key) =>
        key.StartsWith("Search:", StringComparison.OrdinalIgnoreCase)
            ? "searched it"
            : key.StartsWith("Command:", StringComparison.OrdinalIgnoreCase)
                ? "sent it a command"
                : "inspected it";

    private static string TitleFor(string key, SensorProfile? profile)
    {
        var name = profile?.DisplayName ?? SubjectOf(key) ?? "device";

        if (key.StartsWith("Command:", StringComparison.OrdinalIgnoreCase))
        {
            var command = CommandOf(key);
            return command is null ? $"Check {name}" : $"{command} on {name}";
        }

        return $"Check {name}";
    }
}