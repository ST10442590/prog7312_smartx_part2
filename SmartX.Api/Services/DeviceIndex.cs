using SmartX.Shared.Commands;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

// Lookup structures for the live mesh.
//
//   Dictionary<string, SensorProfile>        MAC address -> device, O(1)
//   Dictionary<SensorCategory, List<string>> category -> devices, for filters
//   SortedDictionary<DateTimeOffset, ...>    timeline, always in time order
//
// The point of the MAC dictionary is what it replaces. Resolving an incoming
// packet by scanning a list of registered devices is O(n) per packet: at
// 24 devices that is cheap, but the brief describes thousands of nodes, and
// the cost would be paid on every single packet. A hash lookup does not care
// how many devices exist.
public sealed class DeviceIndex
{
    private readonly DeviceRegistry _registry;
    private readonly Lock _gate = new();

    // Primary lookup. Keyed on MAC address because that is what a physical
    // node actually identifies itself by on the wire.
    private readonly Dictionary<string, SensorProfile> _byMac =
        new(StringComparer.OrdinalIgnoreCase);

    // Secondary lookup, so filtering by category does not mean scanning
    // every device. Built once and maintained, not recomputed per request.
    private readonly Dictionary<SensorCategory, List<string>> _byCategory = new();

    // Timeline. A SortedDictionary keeps keys in order as they are inserted,
    // so the dashboard can render a graph without sorting anything first.
    // Packets usually arrive in time order, but not always — a delayed node
    // can report late, and a plain Dictionary would leave that reading out
    // of sequence.
    private readonly SortedDictionary<DateTimeOffset, TimelinePoint> _timeline = new();

    private const int MaxTimelinePoints = 600;

    private long _lookupHits;
    private long _lookupMisses;
    private long _rebuilds;

    public DeviceIndex(DeviceRegistry registry) => _registry = registry;

    // One moment on the timeline: how many packets and alerts landed in it.
    public sealed class TimelinePoint
    {
        public DateTimeOffset BucketUtc { get; set; }
        public int PacketCount { get; set; }
        public int AlertCount { get; set; }
        public double MeanValue { get; set; }

        // Running total, so MeanValue stays correct as packets are added
        // without having to keep every value in the bucket.
        internal double Sum { get; set; }
    }

    // Rebuilds both lookups from the registry. Called when a device is
    // registered, since the index must not drift from the source of truth.
    public void Rebuild()
    {
        lock (_gate)
        {
            _byMac.Clear();
            _byCategory.Clear();

            foreach (var profile in _registry.AllProfiles())
            {
                if (!string.IsNullOrWhiteSpace(profile.MacAddress))
                {
                    _byMac[profile.MacAddress] = profile;
                }

                if (!_byCategory.TryGetValue(profile.Category, out var bucket))
                {
                    bucket = [];
                    _byCategory[profile.Category] = bucket;
                }
                bucket.Add(profile.Id);
            }

            _rebuilds++;
        }
    }

    // O(1) resolution of a MAC address to its device.
    public SensorProfile? ByMac(string macAddress)
    {
        lock (_gate)
        {
            if (_byMac.TryGetValue(macAddress, out var profile))
            {
                _lookupHits++;
                return profile;
            }

            _lookupMisses++;
            return null;
        }
    }

    // Device ids in one category, without scanning the full registry.
    public IReadOnlyList<string> ByCategory(SensorCategory category)
    {
        lock (_gate)
        {
            return _byCategory.TryGetValue(category, out var ids)
                ? [.. ids]
                : [];
        }
    }

    // ---------------------------------------------------------- timeline

    // Records a packet against its second. Bucketing by second keeps the
    // timeline bounded — without it, one key per packet would mean thousands
    // of points for a graph that is only a few hundred pixels wide.
    public void Record(ITelemetryPacket packet)
    {
        var bucket = Truncate(packet.CapturedAtUtc);

        lock (_gate)
        {
            if (!_timeline.TryGetValue(bucket, out var point))
            {
                point = new TimelinePoint { BucketUtc = bucket };
                _timeline[bucket] = point;
            }

            point.PacketCount++;
            point.Sum += packet.NumericValue;
            point.MeanValue = Math.Round(point.Sum / point.PacketCount, 3);

            TrimLocked();
        }
    }

    public void RecordAlert(DateTimeOffset raisedUtc)
    {
        var bucket = Truncate(raisedUtc);

        lock (_gate)
        {
            if (!_timeline.TryGetValue(bucket, out var point))
            {
                point = new TimelinePoint { BucketUtc = bucket };
                _timeline[bucket] = point;
            }

            point.AlertCount++;
            TrimLocked();
        }
    }

    // Drops the oldest buckets once the window is full. Because the keys are
    // sorted, the oldest is always first — no search required.
    private void TrimLocked()
    {
        while (_timeline.Count > MaxTimelinePoints)
        {
            _timeline.Remove(_timeline.Keys.First());
        }
    }

    // The timeline, already in chronological order. No sort is performed
    // here: that ordering is a property of the structure itself.
    public IReadOnlyList<TimelinePoint> Timeline(int take = 120)
    {
        lock (_gate)
        {
            return _timeline.Count <= take
                ? [.. _timeline.Values]
                : [.. _timeline.Values.Skip(_timeline.Count - take)];
        }
    }

    // Range query. SortedDictionary has no built-in range API, but because
    // the keys are ordered the scan stops as soon as it passes the end of
    // the range rather than examining every entry.
    public IReadOnlyList<TimelinePoint> Between(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            var results = new List<TimelinePoint>();

            foreach (var (key, point) in _timeline)
            {
                if (key < from) continue;
                if (key > to) break;
                results.Add(point);
            }

            return results;
        }
    }

    public IndexStatsDto Stats()
    {
        lock (_gate)
        {
            return new IndexStatsDto
            {
                IndexedDevices = _byMac.Count,
                IndexedCategories = _byCategory.Count,
                TimelinePoints = _timeline.Count,
                LookupHits = _lookupHits,
                LookupMisses = _lookupMisses,
                Rebuilds = _rebuilds,
                EarliestBucketUtc = _timeline.Count > 0
                    ? _timeline.Keys.First().ToString("HH:mm:ss") : null,
                LatestBucketUtc = _timeline.Count > 0
                    ? _timeline.Keys.Last().ToString("HH:mm:ss") : null
            };
        }
    }

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day,
            value.Hour, value.Minute, value.Second, value.Offset);
}