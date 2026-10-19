using SmartX.Shared.Sensors;

namespace SmartX.Shared.Commands;

// The manual override actions an operator can issue to a device.
public enum CommandKind
{
    TogglePump = 0,
    ToggleRelay = 1,
    OpenValve = 2,
    CloseValve = 3,
    ResetNode = 4,
    ShutdownZone = 5
}

// How urgent an alert is. The numeric values are the priority used by the
// alert queue, and lower means more urgent — which is what
// <see cref="PriorityQueue{TElement,TPriority}"/> dequeues first.
public enum AlertSeverity
{
    Critical = 0,
    High = 1,
    Elevated = 2,
    Informational = 3
}

// One manual override issued by an operator.
// <remarks>
// Commands are immutable once issued. The undo stack holds the commands
// themselves, so reverting means pushing the inverse rather than mutating
// history — an audit trail has to stay intact to be worth keeping.
// </remarks>
public sealed class DeviceCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public CommandKind Kind { get; set; }
    public DateTimeOffset IssuedUtc { get; set; } = DateTimeOffset.UtcNow;

    // The state the device was in before this command ran.
    public bool PreviousState { get; set; }

    // The state this command put it in.
    public bool NewState { get; set; }

    // True when this command was itself produced by an undo.
    public bool IsRevert { get; set; }

    // The command that reverses this one. Used by the undo stack.
    public DeviceCommand Inverse() => new()
    {
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        Kind = Kind switch
        {
            CommandKind.OpenValve => CommandKind.CloseValve,
            CommandKind.CloseValve => CommandKind.OpenValve,
            _ => Kind
        },
        PreviousState = NewState,
        NewState = PreviousState,
        IsRevert = true
    };

    public string Describe() =>
        $"{Kind} on {DeviceName} → {(NewState ? "on" : "off")}";
}

// An alert raised by the ingestion pipeline. Critical alerts bypass the
// standard FIFO queue and are processed first.
public sealed class DeviceAlert
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string LocationPath { get; set; } = string.Empty;
    public SensorCategory Category { get; set; }
    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public double ZScore { get; set; }
    public DateTimeOffset RaisedUtc { get; set; } = DateTimeOffset.UtcNow;

    // Identity for duplicate suppression. A node that drops out for ninety
    // seconds would otherwise raise the same alert on every poll, so the
    // active-alert set keys on this rather than on <see cref="Id"/>.
    public string DedupeKey => $"{DeviceId}|{Severity}|{AlertType}";

    // Coarse type, used for dedupe and for pattern analysis.
    public string AlertType { get; set; } = string.Empty;
}