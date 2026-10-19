namespace SmartX.Shared.Commands;

// Counters showing the lookup structures doing their job.
public sealed class IndexStatsDto
{
    // Devices in the MAC-keyed dictionary.
    public int IndexedDevices { get; set; }

    // Distinct categories in the secondary index.
    public int IndexedCategories { get; set; }

    // Buckets currently held in the sorted timeline.
    public int TimelinePoints { get; set; }

    // Successful O(1) MAC resolutions.
    public long LookupHits { get; set; }

    // Lookups for a MAC that is not registered.
    public long LookupMisses { get; set; }

    // Times the index was rebuilt from the registry.
    public long Rebuilds { get; set; }

    public string? EarliestBucketUtc { get; set; }
    public string? LatestBucketUtc { get; set; }
}