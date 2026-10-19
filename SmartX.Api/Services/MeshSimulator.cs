using SmartX.Shared.Commands;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

// Simulates a mesh of ESP32 nodes publishing telemetry continuously.
//
// The brief treats the physical mesh as a simulation, so this service stands
// in for it: it registers a fictional deployment on startup and then
// publishes readings on a timer for as long as the API runs.
//
// Each node has its own baseline and noise level, so the dashboard shows
// genuinely heterogeneous data rather than one repeated waveform. Faults are
// injected at a low probability so the Pulse Grid and the alert queue can be
// seen reacting live during a demo.
public sealed class MeshSimulator : BackgroundService
{
    private readonly DeviceRegistry _registry;
    private readonly TelemetryArchive _archive;
    private readonly CommandStreamService _stream;
    private readonly DeviceIndex _index;
    private readonly SuggestionEngine _engine;
    private readonly ILogger<MeshSimulator> _logger;

    // Seeded so every run produces the same sequence. A demo that behaves
    // differently each time is impossible to narrate.
    private readonly Random _random = new(20260914);

    private readonly List<SimulatedNode> _nodes = new();

    // How often the mesh publishes a round of telemetry.
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(800);

    // How often dismissed problems become suggestible again. A device
    // glanced at five minutes ago and still broken deserves raising twice.
    private static readonly TimeSpan AttentionLifetime = TimeSpan.FromMinutes(5);

    // Chance per node per round of an injected anomalous spike.
    //
    // Tuned so the mesh sits mostly nominal with occasional faults. Higher
    // values prove the detection works but leave the grid permanently
    // coloured, which defeats the point: if everything is amber, amber stops
    // carrying information.
    private const double SpikeChance = 0.0015;

    // Chance per node per round of the node going silent.
    private const double DropoutChance = 0.0006;

    private DateTimeOffset _lastAttentionExpiry = DateTimeOffset.UtcNow;

    public MeshSimulator(
        DeviceRegistry registry,
        TelemetryArchive archive,
        CommandStreamService stream,
        DeviceIndex index,
        SuggestionEngine engine,
        ILogger<MeshSimulator> logger)
    {
        _registry = registry;
        _archive = archive;
        _stream = stream;
        _index = index;
        _engine = engine;
        _logger = logger;
    }

    // One simulated device and the shape of the signal it produces.
    private sealed class SimulatedNode
    {
        public required SensorProfile Profile { get; init; }
        public required PayloadKind Kind { get; init; }
        public required double Baseline { get; init; }
        public required double Noise { get; init; }
        public long Sequence { get; set; }

        // Rounds remaining before this node starts publishing again.
        public int SilentRounds { get; set; }

        // True while this node has an unresolved dropout alert, so the
        // recovery clear only fires once.
        public bool DropoutReported { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SeedMesh();

        // The index mirrors the registry, so it has to be built once the
        // devices exist.
        _index.Rebuild();

        _logger.LogInformation(
            "Mesh simulator started with {Count} nodes.", _nodes.Count);

        // PeriodicTimer does not drift the way a Task.Delay loop does, and it
        // cancels cleanly on shutdown.
        using var timer = new PeriodicTimer(PublishInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                PublishRound();

                // Drain the queues. Alerts first and in full, then whatever
                // standard-queue budget is left.
                _stream.ProcessTick();

                ExpireAttentionIfDue();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("Mesh simulator stopped.");
    }

    private void ExpireAttentionIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAttentionExpiry < AttentionLifetime) return;

        _engine.ExpireAttention();
        _lastAttentionExpiry = now;
    }

    // Registers the fictional deployment.
    private void SeedMesh()
    {
        // Facility A -> Zone 1 / Zone 2 -> Sub-Zone A / Sub-Zone B
        var layout = new[]
        {
            ("Zone 1", "Sub-Zone A"),
            ("Zone 1", "Sub-Zone B"),
            ("Zone 2", "Sub-Zone A"),
            ("Zone 2", "Sub-Zone B")
        };

        var index = 0;

        foreach (var (zone, subZone) in layout)
        {
            // Three environmental, two power, one actuator per sub-zone:
            // twenty-four channels in total across the mesh.
            AddNode(index++, zone, subZone, SensorCategory.Environmental,
                TelemetryUnit.Celsius, PayloadKind.Float, baseline: 21.5, noise: 0.6);

            AddNode(index++, zone, subZone, SensorCategory.Environmental,
                TelemetryUnit.Percent, PayloadKind.Float, baseline: 62.0, noise: 2.4);

            AddNode(index++, zone, subZone, SensorCategory.Environmental,
                TelemetryUnit.Lux, PayloadKind.Float, baseline: 480.0, noise: 35.0);

            AddNode(index++, zone, subZone, SensorCategory.PowerConsumption,
                TelemetryUnit.Watt, PayloadKind.Integer, baseline: 1450, noise: 90);

            AddNode(index++, zone, subZone, SensorCategory.PowerConsumption,
                TelemetryUnit.KiloWattHour, PayloadKind.Float, baseline: 4.2, noise: 0.35);

            AddNode(index++, zone, subZone, SensorCategory.Actuator,
                TelemetryUnit.Boolean, PayloadKind.Boolean, baseline: 1, noise: 0);
        }
    }

    private void AddNode(
        int index, string zone, string subZone,
        SensorCategory category, TelemetryUnit unit, PayloadKind kind,
        double baseline, double noise)
    {
        var suffix = $"{(char)('A' + index % 26)}{index:00}";

        var profile = new SensorProfile
        {
            Id = $"ESP32-{suffix}",
            MacAddress = FormatMac(index),
            DisplayName = $"ESP32-{suffix}",
            Facility = "Facility A",
            Zone = zone,
            SubZone = subZone,
            NodeId = $"node-{index:000}",
            Category = category,
            Unit = unit,
            RequiresMainsPower = category == SensorCategory.Actuator
        };

        if (!_registry.TryRegister(profile, out var errors))
        {
            _logger.LogWarning(
                "Simulated node {Id} failed validation: {Errors}",
                profile.Id, string.Join("; ", errors));
            return;
        }

        _nodes.Add(new SimulatedNode
        {
            Profile = profile,
            Kind = kind,
            Baseline = baseline,
            Noise = noise
        });
    }

    private static string FormatMac(int index) =>
        $"5C:CF:7F:{index / 256 % 256:X2}:{index % 256:X2}:{(index * 7) % 256:X2}";

    // Publishes one round as a batch. Each node's packet is collected into a
    // list first, then transmitted in a single call — which is how a real
    // gateway forwards a buffered window rather than one packet at a time.
    private void PublishRound()
    {
        var batch = new List<ITelemetryPacket>(_nodes.Count);

        foreach (var node in _nodes)
        {
            // A node in a dropout window publishes nothing at all. The Pulse
            // Grid notices the absence rather than being told about it.
            if (node.SilentRounds > 0)
            {
                node.SilentRounds--;

                // The dropout alert is raised once per outage, not once per
                // round. The HashSet would suppress the repeats anyway, but
                // not even attempting them keeps the suppression count
                // meaningful rather than inflated by this loop.
                if (!node.DropoutReported)
                {
                    node.DropoutReported = true;

                    _stream.RaiseAlert(new DeviceAlert
                    {
                        DeviceId = node.Profile.Id,
                        DeviceName = node.Profile.DisplayName,
                        LocationPath = node.Profile.LocationPath,
                        Category = node.Profile.Category,
                        Severity = AlertSeverity.High,
                        AlertType = "disconnect",
                        Message = "Node stopped publishing — no heartbeat."
                    });

                    _index.RecordAlert(DateTimeOffset.UtcNow);

                    // The alert is a situation the operator may respond to,
                    // so the engine needs to see it to learn what follows.
                    _engine.Record(new ActivityEvent
                    {
                        Kind = ActivityKind.Alert,
                        Subject = node.Profile.Id,
                        Detail = "disconnect"
                    });
                }
                continue;
            }

            // The node is publishing again, so clear its outage conditions
            // and let it alarm afresh if it fails later.
            if (node.DropoutReported)
            {
                node.DropoutReported = false;
                _stream.ClearAlertsFor(node.Profile.Id);
                _logger.LogInformation("Node {Id} recovered.", node.Profile.Id);
            }

            if (_random.NextDouble() < DropoutChance)
            {
                node.SilentRounds = _random.Next(50, 110);
                _logger.LogInformation("Node {Id} dropped out.", node.Profile.Id);
                continue;
            }

            var dto = new TelemetryPacketDto
            {
                DeviceId = node.Profile.Id,
                Kind = node.Kind,
                Value = NextValue(node),
                Category = node.Profile.Category,
                Unit = node.Profile.Unit,
                Sequence = ++node.Sequence,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };

            var packet = dto.ToPacket();
            batch.Add(packet);

            // Standard path: join the back of the FIFO queue.
            _stream.EnqueuePacket(packet);

            // Timeline bucket for the dashboard graph.
            _index.Record(packet);

            // Live scoring for the Pulse Grid, which also tells us whether
            // this reading warrants an alert.
            var score = _registry.Observe(packet);
            if (score is { } z) MaybeRaiseAlert(node, z);
        }

        // Batch transmission into the historical archive.
        if (batch.Count > 0) _archive.Ingest(batch, TelemetryUnit.None);
    }

    // Converts a z-score into an alert, if it is far enough out. Severity
    // decides queue position, so a severe power spike is dequeued ahead of a
    // merely elevated environmental reading no matter which arrived first.
    private void MaybeRaiseAlert(SimulatedNode node, double z)
    {
        var magnitude = Math.Abs(z);

        var alertType = node.Profile.Category == SensorCategory.PowerConsumption
            ? "power-spike"
            : "reading-anomaly";

        // The node is back within normal range, so clear its anomaly
        // condition and let it alarm again if it spikes later. Without this
        // the active-alert set grows forever and a device could only ever
        // raise each alert type once for the lifetime of the process.
        if (magnitude < 1.5)
        {
            _stream.ClearAlert($"{node.Profile.Id}|{AlertSeverity.Critical}|{alertType}");
            _stream.ClearAlert($"{node.Profile.Id}|{AlertSeverity.High}|{alertType}");
            return;
        }

        if (magnitude < 3.0) return;

        var severity = magnitude >= 5.0
            ? AlertSeverity.Critical
            : AlertSeverity.High;

        var direction = z > 0 ? "above" : "below";

        var queued = _stream.RaiseAlert(new DeviceAlert
        {
            DeviceId = node.Profile.Id,
            DeviceName = node.Profile.DisplayName,
            LocationPath = node.Profile.LocationPath,
            Category = node.Profile.Category,
            Severity = severity,
            ZScore = Math.Round(z, 2),
            AlertType = alertType,
            Message = $"Reading {magnitude:0.0} deviations {direction} baseline."
        });

        // Only record it if it was actually queued, so a suppressed
        // duplicate neither inflates the graph nor pollutes the model with a
        // situation the operator never actually saw.
        if (queued)
        {
            _index.RecordAlert(DateTimeOffset.UtcNow);

            _engine.Record(new ActivityEvent
            {
                Kind = ActivityKind.Alert,
                Subject = node.Profile.Id,
                Detail = alertType
            });
        }
    }

    private double NextValue(SimulatedNode node)
    {
        if (node.Kind == PayloadKind.Boolean)
        {
            // Valves mostly hold state, flipping occasionally.
            return _random.NextDouble() < 0.03 ? 0 : 1;
        }

        // Gaussian noise via Box-Muller, so the baseline looks like real
        // sensor scatter rather than a uniform band.
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        var gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) *
                       Math.Sin(2.0 * Math.PI * u2);

        var value = node.Baseline + gaussian * node.Noise;

        if (_random.NextDouble() < SpikeChance)
        {
            // Six to ten times the usual noise, comfortably past the spike
            // threshold, and signed so spikes go both ways.
            var direction = _random.NextDouble() < 0.5 ? -1 : 1;
            value += direction * node.Noise * _random.Next(6, 11);
            _logger.LogInformation("Injected spike on {Id}.", node.Profile.Id);
        }

        return value;
    }
}