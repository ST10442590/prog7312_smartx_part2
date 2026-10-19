namespace SmartX.Shared.Commands;

// Live state of the processing pipeline, as shown on the Command Stream
// dashboard. Exposes the depth of each structure so the queues can be seen
// filling and draining rather than only described.
public sealed class StreamSnapshotDto
{
    // Packets waiting in the standard FIFO queue.
    public int StandardQueueDepth { get; set; }

    // Alerts waiting in the priority queue.
    public int AlertQueueDepth { get; set; }

    // Commands available to undo.
    public int UndoDepth { get; set; }

    // Distinct alert conditions currently active.
    public int ActiveAlertCount { get; set; }

    public long PacketsProcessed { get; set; }
    public long AlertsRaised { get; set; }

    // Alerts the dedupe set suppressed. This is the number that shows the
    // HashSet earning its place — without it, one node dropping out for
    // ninety seconds would raise the same alert on every poll.
    public long DuplicatesSuppressed { get; set; }

    public long CommandsIssued { get; set; }
    public long CommandsReverted { get; set; }

    // Most recent packets drained, newest first.
    public List<ProcessedPacketDto> RecentlyProcessed { get; set; } = [];

    // Alerts currently queued, most urgent first.
    public List<DeviceAlert> PendingAlerts { get; set; } = [];

    // The undo stack, top first.
    public List<DeviceCommand> UndoHistory { get; set; } = [];
}

// One packet that came off the standard queue.
public sealed class ProcessedPacketDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public double Value { get; set; }
    public DateTimeOffset ProcessedUtc { get; set; }

    // Position in the FIFO order, so the ordering is visible.
    public long Sequence { get; set; }
}

// What the client sends to issue a manual override.
public sealed class IssueCommandDto
{
    public string DeviceId { get; set; } = string.Empty;
    public CommandKind Kind { get; set; }
    public bool NewState { get; set; }
}

// Result of issuing or reverting a command.
public sealed class CommandResultDto
{
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public DeviceCommand? Command { get; set; }
    public StreamSnapshotDto? Snapshot { get; set; }
}