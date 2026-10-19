using SmartX.Api.Services;
using SmartX.Shared.Commands;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// The registry is a singleton because it holds live mesh state shared by
// every request and by the background simulator.
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<TelemetryArchive>();
builder.Services.AddSingleton<CommandStreamService>();
builder.Services.AddSingleton<DeviceIndex>();
builder.Services.AddSingleton<SuggestionEngine>();
builder.Services.AddScoped<TopologyBuilder>();
builder.Services.AddHostedService<MeshSimulator>();

builder.Services.AddOpenApi();

// The Blazor client is served from a different origin during development,
// so it needs an explicit CORS allowance. The policy is deliberately narrow
// rather than AllowAnyOrigin.
const string ClientCors = "SmartXClient";
builder.Services.AddCors(options =>
{
    options.AddPolicy(ClientCors, policy => policy
        .WithOrigins(
            "https://localhost:7296",
            "http://localhost:5006")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(ClientCors);

// ---------------------------------------------------------------- health
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "online",
    service = "Smart-X Ingestion Gateway",
    utc = DateTimeOffset.UtcNow
}))
.WithName("GetHealth")
.WithSummary("Liveness probe for the gateway.");

// ------------------------------------------------------------ ingestion
app.MapPost("/api/telemetry", (
    TelemetryPacketDto dto,
    DeviceRegistry registry,
    TelemetryArchive archive,
    CommandStreamService stream,
    DeviceIndex index) =>
{
    var errors = dto.Validate();
    if (errors.Count > 0)
    {
        return Results.BadRequest(new { errors });
    }

    var packet = dto.ToPacket();
    var score = registry.Observe(packet);

    // A packet from an unregistered node is a configuration error, not a
    // silent no-op, so it gets a 404 rather than a 200.
    if (score is null)
    {
        return Results.NotFound(new { error = $"Device '{dto.DeviceId}' is not registered." });
    }

    archive.Ingest([packet], dto.Unit);
    stream.EnqueuePacket(packet);
    index.Record(packet);

    return Results.Ok(new { dto.DeviceId, zScore = Math.Round(score.Value, 2) });
})
.WithName("IngestTelemetry")
.WithSummary("Receives a single telemetry packet from a sensor node.");

app.MapPost("/api/telemetry/batch", (
    TelemetryPacketDto[] packets,
    DeviceRegistry registry,
    TelemetryArchive archive,
    CommandStreamService stream,
    DeviceIndex index) =>
{
    var accepted = new List<ITelemetryPacket>(packets.Length);
    var rejected = new List<string>();

    foreach (var dto in packets)
    {
        if (dto.Validate().Count > 0)
        {
            rejected.Add(dto.DeviceId);
            continue;
        }

        var packet = dto.ToPacket();
        if (registry.Observe(packet) is null)
        {
            rejected.Add(dto.DeviceId);
            continue;
        }

        accepted.Add(packet);
        stream.EnqueuePacket(packet);
        index.Record(packet);
    }

    archive.Ingest(accepted, TelemetryUnit.None);

    return Results.Ok(new
    {
        accepted = accepted.Count,
        rejected,
        stats = archive.Stats()
    });
})
.WithName("IngestTelemetryBatch")
.WithSummary("Receives a buffered batch of telemetry packets.");

// ------------------------------------------------------------- devices
app.MapGet("/api/devices", (DeviceRegistry registry) =>
    Results.Ok(registry.AllProfiles()))
.WithName("GetDevices")
.WithSummary("Lists every registered sensor profile.");

app.MapGet("/api/devices/{id}", (
    string id, DeviceRegistry registry, SuggestionEngine engine) =>
{
    var profile = registry.GetProfile(id);
    if (profile is null) return Results.NotFound();

    // Opening a device is an operator action worth learning from.
    engine.Record(new ActivityEvent
    {
        Kind = ActivityKind.DeviceInspect,
        Subject = id
    });

    return Results.Ok(profile);
})
.WithName("GetDevice")
.WithSummary("Returns one sensor profile.");

app.MapPost("/api/devices", (
    SensorProfile profile, DeviceRegistry registry, DeviceIndex index) =>
{
    if (!registry.TryRegister(profile, out var errors))
    {
        return Results.BadRequest(new { errors });
    }

    // The lookup index mirrors the registry, so it must not drift from it.
    index.Rebuild();

    return Results.Created($"/api/devices/{profile.Id}", profile);
})
.WithName("RegisterDevice")
.WithSummary("Registers a new sensor, or updates an existing profile.");

// --------------------------------------------------------- pulse grid
app.MapGet("/api/mesh/health", (DeviceRegistry registry) =>
    Results.Ok(registry.Snapshot(DateTimeOffset.UtcNow)))
.WithName("GetMeshHealth")
.WithSummary("Current Pulse Grid state for every device, worst first.");

app.MapGet("/api/mesh/summary", (DeviceRegistry registry) =>
    Results.Ok(registry.Summarise(DateTimeOffset.UtcNow)))
.WithName("GetMeshSummary")
.WithSummary("Mesh-wide health counters.");

// ------------------------------------------------------- attachments
app.MapPost("/api/devices/{id}/attachments", async (
    string id,
    IFormFile file,
    DeviceRegistry registry,
    IWebHostEnvironment env) =>
{
    if (registry.GetProfile(id) is null)
    {
        return Results.NotFound(new { errors = new[] { $"Device '{id}' is not registered." } });
    }

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { errors = new[] { "No file was supplied." } });
    }

    const long maxBytes = 25 * 1024 * 1024;
    if (file.Length > maxBytes)
    {
        return Results.BadRequest(new { errors = new[] { "Files are limited to 25 MB." } });
    }

    // Only the extensions a device profile legitimately needs.
    string[] allowed = [".json", ".yaml", ".yml", ".txt", ".log", ".csv",
                        ".cfg", ".ini", ".png", ".jpg", ".jpeg", ".webp"];

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!allowed.Contains(extension))
    {
        return Results.BadRequest(new
        {
            errors = new[] { $"'{extension}' files are not accepted." }
        });
    }

    var root = Path.Combine(env.ContentRootPath, "uploads", id);
    Directory.CreateDirectory(root);

    // The stored name is generated, never taken from the client. A filename
    // like "../../appsettings.json" would otherwise escape the upload folder.
    var storedName = $"{Guid.NewGuid():N}{extension}";
    var storedPath = Path.Combine(root, storedName);

    string hash;
    await using (var destination = File.Create(storedPath))
    {
        await using var source = file.OpenReadStream();
        await source.CopyToAsync(destination);

        destination.Position = 0;
        using var sha = System.Security.Cryptography.SHA256.Create();
        hash = Convert.ToHexString(await sha.ComputeHashAsync(destination));
    }

    var attachment = new SensorAttachment
    {
        FileName = Path.GetFileName(file.FileName),
        ContentType = file.ContentType,
        SizeBytes = file.Length,
        StoredPath = Path.Combine("uploads", id, storedName),
        Sha256 = hash
    };

    return registry.TryAttach(id, attachment)
        ? Results.Ok(attachment)
        : Results.NotFound(new { errors = new[] { $"Device '{id}' is not registered." } });
})
.WithName("UploadAttachment")
.WithSummary("Attaches a config file, photo or hardware log to a sensor profile.")
.DisableAntiforgery();

app.MapGet("/api/devices/{id}/attachments", (string id, DeviceRegistry registry) =>
{
    var profile = registry.GetProfile(id);
    return profile is null
        ? Results.NotFound()
        : Results.Ok(profile.Attachments);
})
.WithName("GetAttachments")
.WithSummary("Lists files attached to a sensor profile.");

// --------------------------------------------------------- topology
app.MapGet("/api/topology/validate", (TopologyBuilder topology) =>
{
    var report = topology.Validate();

    return Results.Ok(new
    {
        report.IsValid,
        report.ErrorCount,
        report.WarningCount,
        report.NodesVisited,
        report.MaxDepthReached,
        Issues = report.Issues
    });
})
.WithName("ValidateTopology")
.WithSummary("Recursively validates the deployment tree.");

app.MapGet("/api/topology/tree", (TopologyBuilder topology) =>
    Results.Ok(topology.Build()))
.WithName("GetTopologyTree")
.WithSummary("Returns the deployment tree assembled from registered devices.");

app.MapGet("/api/topology/path/{deviceId}", (
    string deviceId, TopologyBuilder topology) =>
{
    var path = topology.PathTo(deviceId);
    return path is null
        ? Results.NotFound(new { error = $"'{deviceId}' is not in the tree." })
        : Results.Ok(new { deviceId, path });
})
.WithName("GetDevicePath")
.WithSummary("Recursively resolves a device's ancestry path.");

// ------------------------------------------------------ diagnostics
app.MapGet("/api/diagnostics/stats", (TelemetryArchive archive) =>
    Results.Ok(archive.Stats()))
.WithName("GetPipelineStats")
.WithSummary("Ingestion and storage counters.");

app.MapPost("/api/diagnostics/flush", (TelemetryArchive archive) =>
{
    var moved = archive.Flush();
    return Results.Ok(new { moved, stats = archive.Stats() });
})
.WithName("FlushArchive")
.WithSummary("Drains staged batches into the historical archive.");

app.MapGet("/api/diagnostics/search/value", (
    TelemetryArchive archive,
    string? deviceId, double min, double max) =>
    Results.Ok(archive.SearchByValue(deviceId, min, max)))
.WithName("SearchByValue")
.WithSummary("Linear scan over historical readings.");

app.MapGet("/api/diagnostics/search/time", (
    TelemetryArchive archive, int secondsAgo) =>
    Results.Ok(archive.SearchByTime(
        DateTimeOffset.UtcNow.AddSeconds(-Math.Abs(secondsAgo)))))
.WithName("SearchByTime")
.WithSummary("Binary search over the time-ordered archive.");

app.MapPost("/api/diagnostics/aggregate", (
    TelemetryArchive archive, string[] deviceIds) =>
    Results.Ok(archive.AggregateLoad(deviceIds)))
.WithName("AggregateLoad")
.WithSummary("Aggregates meters using the overloaded + operator.");

app.MapGet("/api/diagnostics/delta", (
    TelemetryArchive archive, string left, string right) =>
    Results.Ok(archive.Delta(left, right)))
.WithName("DeltaReadings")
.WithSummary("Delta between two sensors using the overloaded - operator.");

app.MapGet("/api/diagnostics/benchmark", () =>
    Results.Ok(TelemetryArchive.Benchmark([1_000, 10_000, 50_000, 200_000])))
.WithName("RunBenchmark")
.WithSummary("Times insertion and search against growing volumes.");

// ----------------------------------------------------- command stream
app.MapGet("/api/stream/snapshot", (CommandStreamService stream) =>
    Results.Ok(stream.Snapshot()))
.WithName("GetStreamSnapshot")
.WithSummary("Current depths and contents of the processing structures.");

// Issues a manual override, pushes it onto the undo stack, and records it
// as an operator action the suggestion engine can learn from.
app.MapPost("/api/stream/command", (
    IssueCommandDto request,
    CommandStreamService stream,
    SuggestionEngine engine) =>
{
    var result = stream.Issue(request);

    if (result.Succeeded)
    {
        engine.Record(new ActivityEvent
        {
            Kind = ActivityKind.Command,
            Subject = request.DeviceId,
            Detail = request.Kind.ToString()
        });
    }

    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
})
.WithName("IssueCommand")
.WithSummary("Issues a manual override command to a device.");

app.MapPost("/api/stream/undo", (CommandStreamService stream) =>
{
    var result = stream.Undo();
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
})
.WithName("UndoCommand")
.WithSummary("Reverts the most recently issued command.");

app.MapPost("/api/stream/simulate-alert", (
    string deviceId, AlertSeverity severity, DeviceRegistry registry,
    CommandStreamService stream, SuggestionEngine engine) =>
{
    var profile = registry.GetProfile(deviceId);
    if (profile is null)
    {
        return Results.NotFound(new { error = $"Device '{deviceId}' is not registered." });
    }

    var queued = stream.RaiseAlert(new DeviceAlert
    {
        DeviceId = profile.Id,
        DeviceName = profile.DisplayName,
        LocationPath = profile.LocationPath,
        Category = profile.Category,
        Severity = severity,
        AlertType = "manual-test",
        Message = $"Operator-injected {severity} alert."
    });

    if (queued)
    {
        engine.Record(new ActivityEvent
        {
            Kind = ActivityKind.Alert,
            Subject = profile.Id,
            Detail = "manual-test"
        });
    }

    // queued = false means the active-alert set suppressed it as a
    // duplicate, which is itself worth surfacing rather than hiding.
    return Results.Ok(new
    {
        queued,
        suppressedAsDuplicate = !queued,
        snapshot = stream.Snapshot()
    });
})
.WithName("SimulateAlert")
.WithSummary("Raises a test alert, demonstrating priority ordering and dedupe.");

// ------------------------------------------------------ lookup index
app.MapGet("/api/index/stats", (DeviceIndex index) =>
    Results.Ok(index.Stats()))
.WithName("GetIndexStats")
.WithSummary("Lookup and timeline index counters.");

app.MapGet("/api/index/by-mac/{mac}", (string mac, DeviceIndex index) =>
{
    var profile = index.ByMac(mac);
    return profile is null
        ? Results.NotFound(new { error = $"No device registered with MAC '{mac}'." })
        : Results.Ok(profile);
})
.WithName("LookupByMac")
.WithSummary("Resolves a MAC address to a device profile in constant time.");

app.MapGet("/api/index/by-category/{category}", (
    SensorCategory category, DeviceIndex index, DeviceRegistry registry) =>
{
    var ids = index.ByCategory(category);

    var profiles = ids
        .Select(registry.GetProfile)
        .Where(p => p is not null)
        .ToList();

    return Results.Ok(new { category, count = profiles.Count, devices = profiles });
})
.WithName("LookupByCategory")
.WithSummary("Lists devices in a category using the secondary index.");

app.MapGet("/api/index/timeline", (DeviceIndex index, int? take) =>
    Results.Ok(index.Timeline(take ?? 120)))
.WithName("GetTimeline")
.WithSummary("Ordered packet and alert timeline for the dashboard graph.");

app.MapGet("/api/index/timeline/range", (
    DeviceIndex index, int fromSecondsAgo, int toSecondsAgo) =>
{
    var now = DateTimeOffset.UtcNow;
    var from = now.AddSeconds(-Math.Abs(fromSecondsAgo));
    var to = now.AddSeconds(-Math.Abs(toSecondsAgo));

    return Results.Ok(index.Between(
        from <= to ? from : to,
        from <= to ? to : from));
})
.WithName("GetTimelineRange")
.WithSummary("Range query over the sorted timeline.");

// -------------------------------------------------- suggestion engine
// Current recommendations, plus the learned model behind them so a
// suggestion can be traced to the observations that produced it.
app.MapGet("/api/suggestions", (SuggestionEngine engine, int? take) =>
    Results.Ok(engine.Report(take ?? 5)))
.WithName("GetSuggestions")
.WithSummary("Proactive suggested actions and the transitions behind them.");

// Device search. The search itself is the signal the engine learns from —
// what the operator looks for, and what they look for next.
app.MapGet("/api/suggestions/search", (
    string query,
    DeviceRegistry registry,
    DeviceIndex index,
    SuggestionEngine engine) =>
{
    var term = query?.Trim() ?? string.Empty;

    var matches = registry.AllProfiles()
        .Where(p =>
            p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            p.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            p.MacAddress.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            p.LocationPath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            p.Category.ToString().Contains(term, StringComparison.OrdinalIgnoreCase))
        .ToList();

    // A search that resolves to exactly one device is recorded against that
    // device, so transitions are learned per node rather than per keyword.
    if (!string.IsNullOrWhiteSpace(term))
    {
        engine.Record(new ActivityEvent
        {
            Kind = ActivityKind.Search,
            Subject = matches.Count == 1 ? matches[0].Id : term
        });
    }

    return Results.Ok(new { query = term, count = matches.Count, devices = matches });
})
.WithName("SearchDevices")
.WithSummary("Searches devices and records the query for pattern analysis.");

// Dismisses a suggestion, so an acknowledged problem stops resurfacing.
app.MapPost("/api/suggestions/attend/{deviceId}", (
    string deviceId, SuggestionEngine engine) =>
{
    engine.MarkAttended(deviceId);
    return Results.Ok(new { deviceId, attended = true });
})
.WithName("MarkAttended")
.WithSummary("Marks a device as being dealt with.");

// Clears the attention set, so unresolved problems resurface.
app.MapPost("/api/suggestions/expire-attention", (SuggestionEngine engine) =>
{
    engine.ExpireAttention();
    return Results.Ok(new { expired = true });
})
.WithName("ExpireAttention")
.WithSummary("Allows previously dismissed problems to be suggested again.");

app.MapPost("/api/suggestions/reset", (SuggestionEngine engine) =>
{
    engine.Reset();
    return Results.Ok(new { reset = true });
})
.WithName("ResetSuggestions")
.WithSummary("Clears the learned model, for demonstrating it learning from scratch.");

app.Run();