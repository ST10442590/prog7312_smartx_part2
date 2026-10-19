using SmartX.Shared.Commands;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

// The real-time processing engine. Four structures, each doing a job the
// others cannot:
//
//   Queue<T>          standard telemetry, processed first-in-first-out
//   PriorityQueue<,>  critical alerts, which bypass the standard queue
//   Stack<T>          manual override history, for undo
//   HashSet<T>        active alert conditions, preventing duplicates
//
// Everything is guarded by one lock. The mesh simulator writes on its own
// timer while HTTP requests read, so concurrent access is guaranteed rather
// than hypothetical.
public sealed class CommandStreamService
{
    private readonly DeviceRegistry _registry;
    private readonly ILogger<CommandStreamService> _logger;
    private readonly Lock _gate = new();

    // FIFO. Telemetry is processed in the order it arrived, because a
    // reading that overtakes an earlier one would corrupt the timeline the
    // archive depends on.
    private readonly Queue<ITelemetryPacket> _standardQueue = new();

    // Priority queue. Dequeues the LOWEST priority value first, so a
    // Critical alert (severity 0) comes out ahead of an Informational one
    // (severity 3) regardless of arrival order.
    private readonly PriorityQueue<DeviceAlert, long> _alertQueue = new();

    // LIFO. The last command issued is the first one undone, which is what
    // an operator expects from "revert the last action".
    private readonly Stack<DeviceCommand> _undoStack = new();

    // Duplicate suppression. A node that drops out stays dropped out, so
    // without this it would raise the same alert on every single poll.
    private readonly HashSet<string> _activeAlertKeys =
        new(StringComparer.OrdinalIgnoreCase);

    // Current on/off state per actuator, so a command knows what it is
    // changing from and the inverse can be built correctly.
    private readonly Dictionary<string, bool> _actuatorState =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ProcessedPacketDto> _recentlyProcessed = [];
    private const int RecentWindow = 12;

    private long _packetsProcessed;
    private long _alertsRaised;
    private long _duplicatesSuppressed;
    private long _commandsIssued;
    private long _commandsReverted;
    private long _alertSequence;

    public CommandStreamService(
        DeviceRegistry registry,
        ILogger<CommandStreamService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    // ----------------------------------------------------------- ingress

    // Standard path. Packets join the back of the FIFO queue and wait their
    // turn.
    public void EnqueuePacket(ITelemetryPacket packet)
    {
        lock (_gate) _standardQueue.Enqueue(packet);
    }

    // Priority path. An alert skips the standard queue entirely.
    //
    // Returns false when the alert was suppressed as a duplicate, which is
    // the HashSet doing its job.
    public bool RaiseAlert(DeviceAlert alert)
    {
        lock (_gate)
        {
            // Add returns false if the key is already present, so this is
            // both the check and the insert in one O(1) operation.
            if (!_activeAlertKeys.Add(alert.DedupeKey))
            {
                _duplicatesSuppressed++;
                return false;
            }

            // Composite priority: severity dominates, and the sequence
            // number breaks ties. PriorityQueue is not a stable sort, so
            // without the sequence two Critical alerts could come out in
            // the wrong order relative to each other.
            var priority = (long)alert.Severity * 1_000_000 + _alertSequence++;

            _alertQueue.Enqueue(alert, priority);
            _alertsRaised++;

            _logger.LogInformation(
                "Alert queued: {Severity} on {Device} — {Message}",
                alert.Severity, alert.DeviceName, alert.Message);

            return true;
        }
    }

    // Clears an alert condition, letting that device raise the same alert
    // again later. Without this the set would grow forever and a node that
    // recovered could never alarm a second time.
    public void ClearAlert(string dedupeKey)
    {
        lock (_gate) _activeAlertKeys.Remove(dedupeKey);
    }

    // Clears every active condition for a device, used when it recovers.
    public int ClearAlertsFor(string deviceId)
    {
        lock (_gate)
        {
            return _activeAlertKeys.RemoveWhere(key =>
                key.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase));
        }
    }

    // --------------------------------------------------------- processing

    // One processing tick. Alerts are drained first and completely, then
    // whatever standard-queue budget remains is used. That ordering is the
    // whole point of having two queues: a critical alert never waits behind
    // routine telemetry.
    public int ProcessTick(int budget = 40)
    {
        lock (_gate)
        {
            var processed = 0;

            while (_alertQueue.TryDequeue(out var alert, out _))
            {
                // In a full system this would notify an operator. Here it
                // stays queued for the dashboard to display, which is what
                // makes the priority ordering visible.
                _logger.LogDebug("Processing {Severity} alert on {Device}",
                    alert.Severity, alert.DeviceName);
                processed++;
            }

            while (processed < budget && _standardQueue.TryDequeue(out var packet))
            {
                _packetsProcessed++;
                processed++;

                var profile = _registry.GetProfile(packet.DeviceId);

                _recentlyProcessed.Insert(0, new ProcessedPacketDto
                {
                    DeviceId = packet.DeviceId,
                    DeviceName = profile?.DisplayName ?? packet.DeviceId,
                    Value = Math.Round(packet.NumericValue, 3),
                    ProcessedUtc = DateTimeOffset.UtcNow,
                    Sequence = _packetsProcessed
                });

                if (_recentlyProcessed.Count > RecentWindow)
                {
                    _recentlyProcessed.RemoveRange(
                        RecentWindow, _recentlyProcessed.Count - RecentWindow);
                }
            }

            return processed;
        }
    }

    // ---------------------------------------------------------- commands

    // Issues a manual override and pushes it onto the undo stack.
    public CommandResultDto Issue(IssueCommandDto request)
    {
        var profile = _registry.GetProfile(request.DeviceId);

        if (profile is null)
        {
            return new CommandResultDto
            {
                Succeeded = false,
                Error = $"Device '{request.DeviceId}' is not registered."
            };
        }

        lock (_gate)
        {
            _actuatorState.TryGetValue(request.DeviceId, out var previous);

            var command = new DeviceCommand
            {
                DeviceId = profile.Id,
                DeviceName = profile.DisplayName,
                Kind = request.Kind,
                PreviousState = previous,
                NewState = request.NewState
            };

            _actuatorState[request.DeviceId] = request.NewState;
            _undoStack.Push(command);
            _commandsIssued++;

            _logger.LogInformation("Command issued: {Description}", command.Describe());

            return new CommandResultDto
            {
                Succeeded = true,
                Command = command,
                Snapshot = SnapshotLocked()
            };
        }
    }

    // Pops the most recent command and applies its inverse.
    //
    // The popped command is not re-pushed, so undo walks back through
    // history one step at a time rather than toggling the same action.
    public CommandResultDto Undo()
    {
        lock (_gate)
        {
            if (!_undoStack.TryPop(out var last))
            {
                return new CommandResultDto
                {
                    Succeeded = false,
                    Error = "There is nothing to undo."
                };
            }

            var revert = last.Inverse();
            _actuatorState[revert.DeviceId] = revert.NewState;
            _commandsReverted++;

            _logger.LogInformation("Command reverted: {Description}", revert.Describe());

            return new CommandResultDto
            {
                Succeeded = true,
                Command = revert,
                Snapshot = SnapshotLocked()
            };
        }
    }

    // ---------------------------------------------------------- snapshot

    public StreamSnapshotDto Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    // Caller must already hold the lock.
    private StreamSnapshotDto SnapshotLocked() => new()
    {
        StandardQueueDepth = _standardQueue.Count,
        AlertQueueDepth = _alertQueue.Count,
        UndoDepth = _undoStack.Count,
        ActiveAlertCount = _activeAlertKeys.Count,
        PacketsProcessed = _packetsProcessed,
        AlertsRaised = _alertsRaised,
        DuplicatesSuppressed = _duplicatesSuppressed,
        CommandsIssued = _commandsIssued,
        CommandsReverted = _commandsReverted,
        RecentlyProcessed = [.. _recentlyProcessed],

        // UnorderedItems does not reflect dequeue order, so the display
        // copy is sorted the way the queue would actually release them.
        PendingAlerts = _alertQueue.UnorderedItems
            .OrderBy(entry => entry.Priority)
            .Select(entry => entry.Element)
            .Take(10)
            .ToList(),

        // Stack enumerates top-first, which is the order an operator would
        // undo in.
        UndoHistory = _undoStack.Take(10).ToList()
    };

    public bool StateOf(string deviceId)
    {
        lock (_gate) return _actuatorState.TryGetValue(deviceId, out var on) && on;
    }
}