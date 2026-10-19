using System.Net.Http.Headers;
using System.Net.Http.Json;
using SmartX.Shared.Commands;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Client.Services;

// Every call the dashboard makes to the Smart-X gateway goes through here.
//
// Centralising the HTTP surface keeps the Razor components free of URL
// strings and serialisation detail, and means a change to a route is a
// one-line edit rather than a hunt through the UI. Each method returns a
// result the caller can act on rather than throwing, because a dashboard
// that blanks out when the gateway restarts is worse than one that says the
// gateway is unreachable.
public sealed class SmartXApiClient
{
    private readonly HttpClient _http;

    public SmartXApiClient(HttpClient http) => _http = http;

    // True when the gateway responds to a liveness probe.
    public async Task<bool> IsOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ------------------------------------------------------- pulse grid
    // Current Pulse Grid tiles, worst devices first.
    public async Task<IReadOnlyList<DeviceHealthDto>> GetMeshHealthAsync(
        CancellationToken ct = default)
    {
        try
        {
            var tiles = await _http.GetFromJsonAsync<List<DeviceHealthDto>>(
                "/api/mesh/health", ct);
            return tiles ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A dropped poll is not worth tearing the UI down for; the next
            // tick will pick it up.
            return [];
        }
    }

    // Mesh-wide counters for the dashboard header.
    public async Task<MeshSummaryDto?> GetMeshSummaryAsync(
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<MeshSummaryDto>(
                "/api/mesh/summary", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------- devices
    public async Task<IReadOnlyList<SensorProfile>> GetDevicesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var profiles = await _http.GetFromJsonAsync<List<SensorProfile>>(
                "/api/devices", ct);
            return profiles ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    public async Task<SensorProfile?> GetDeviceAsync(
        string deviceId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SensorProfile>(
                $"/api/devices/{deviceId}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Registers a sensor. Returns the API's validation errors when the
    // server rejects it, so the form can show server-side failures the
    // browser did not catch.
    public async Task<ApiResult> RegisterDeviceAsync(
        SensorProfile profile, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/devices", profile, ct);

            if (response.IsSuccessStatusCode) return ApiResult.Ok();

            var payload = await response.Content
                .ReadFromJsonAsync<ErrorPayload>(cancellationToken: ct);

            return ApiResult.Fail(payload?.Errors ?? ["The gateway rejected the registration."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult.Fail(["Could not reach the gateway. Is the API running?"]);
        }
    }

    // -------------------------------------------------------- telemetry
    // Publishes a single packet, used by the manual test panel.
    public async Task<ApiResult> SendTelemetryAsync(
        TelemetryPacketDto packet, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/telemetry", packet, ct);

            if (response.IsSuccessStatusCode) return ApiResult.Ok();

            var payload = await response.Content
                .ReadFromJsonAsync<ErrorPayload>(cancellationToken: ct);

            return ApiResult.Fail(payload?.Errors ?? ["The gateway rejected the packet."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult.Fail(["Could not reach the gateway."]);
        }
    }

    // ------------------------------------------------------- attachments
    // Uploads a config file, deployment photo or hardware log against a
    // sensor profile, streaming the content rather than buffering it into a
    // byte array first.
    public async Task<ApiResult> UploadAttachmentAsync(
        string deviceId,
        Stream content,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            using var streamContent = new StreamContent(content);

            streamContent.Headers.ContentType =
                new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType)
                    ? "application/octet-stream"
                    : contentType);

            form.Add(streamContent, "file", fileName);

            using var response = await _http.PostAsync(
                $"/api/devices/{deviceId}/attachments", form, ct);

            if (response.IsSuccessStatusCode) return ApiResult.Ok();

            var payload = await response.Content
                .ReadFromJsonAsync<ErrorPayload>(cancellationToken: ct);

            return ApiResult.Fail(payload?.Errors ?? ["The upload was rejected."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResult.Fail(["Could not reach the gateway during upload."]);
        }
    }

    // ------------------------------------------------------ diagnostics
    // Pipeline counters: received, staged, stored.
    public async Task<PipelineStatsDto?> GetStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<PipelineStatsDto>(
                "/api/diagnostics/stats", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Forces staged batches to drain into the archive.
    public async Task<PipelineStatsDto?> FlushAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsync("/api/diagnostics/flush", null, ct);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content
                .ReadFromJsonAsync<FlushPayload>(cancellationToken: ct);
            return payload?.Stats;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Linear scan over historical readings.
    public async Task<SearchResultDto?> SearchByValueAsync(
        string? deviceId, double min, double max, CancellationToken ct = default)
    {
        var query = $"/api/diagnostics/search/value?min={min}&max={max}";
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query += $"&deviceId={Uri.EscapeDataString(deviceId)}";
        }

        try
        {
            return await _http.GetFromJsonAsync<SearchResultDto>(query, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Binary search over the time-ordered archive.
    public async Task<SearchResultDto?> SearchByTimeAsync(
        int secondsAgo, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SearchResultDto>(
                $"/api/diagnostics/search/time?secondsAgo={secondsAgo}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Aggregates meters using the overloaded + operator.
    public async Task<AggregationDto?> AggregateAsync(
        IReadOnlyList<string> deviceIds, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/diagnostics/aggregate", deviceIds, ct);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<AggregationDto>(cancellationToken: ct)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Delta between two sensors using the overloaded - operator.
    public async Task<AggregationDto?> DeltaAsync(
        string left, string right, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AggregationDto>(
                $"/api/diagnostics/delta?left={Uri.EscapeDataString(left)}" +
                $"&right={Uri.EscapeDataString(right)}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Runs the performance harness. Slow by design — it inserts 200,000
    // records — so the caller should show a busy state.
    public async Task<BenchmarkResultDto?> BenchmarkAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<BenchmarkResultDto>(
                "/api/diagnostics/benchmark", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Transmits a generated batch through the ingestion endpoint.
    public async Task<BatchResultDto?> SendBatchAsync(
        IReadOnlyList<TelemetryPacketDto> packets, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/telemetry/batch", packets, ct);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<BatchResultDto>(cancellationToken: ct)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // --------------------------------------------------- command stream
    // Live depths of the FIFO queue, priority queue, undo stack and the
    // active-alert set.
    public async Task<StreamSnapshotDto?> GetStreamSnapshotAsync(
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<StreamSnapshotDto>(
                "/api/stream/snapshot", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Issues a manual override, which also pushes it onto the undo stack.
    public async Task<CommandResultDto?> IssueCommandAsync(
        IssueCommandDto request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/stream/command", request, ct);

            return await response.Content
                .ReadFromJsonAsync<CommandResultDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CommandResultDto
            {
                Succeeded = false,
                Error = "Could not reach the gateway."
            };
        }
    }

    // Pops the most recent command and applies its inverse.
    public async Task<CommandResultDto?> UndoCommandAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsync("/api/stream/undo", null, ct);

            return await response.Content
                .ReadFromJsonAsync<CommandResultDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CommandResultDto
            {
                Succeeded = false,
                Error = "Could not reach the gateway."
            };
        }
    }

    // Injects a test alert, so priority ordering and duplicate suppression
    // can be demonstrated on demand.
    public async Task<AlertTestResultDto?> SimulateAlertAsync(
        string deviceId, AlertSeverity severity, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/stream/simulate-alert" +
                      $"?deviceId={Uri.EscapeDataString(deviceId)}&severity={severity}";

            using var response = await _http.PostAsync(url, null, ct);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content
                .ReadFromJsonAsync<AlertTestResultDto>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // ------------------------------------------------------ suggestions
    public async Task<SuggestionReportDto?> GetSuggestionsAsync(
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SuggestionReportDto>(
                "/api/suggestions", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Searches devices. The search itself trains the suggestion engine, so
    // this is both a query and a signal.
    public async Task<DeviceSearchResultDto?> SearchDevicesAsync(
        string query, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<DeviceSearchResultDto>(
                $"/api/suggestions/search?query={Uri.EscapeDataString(query)}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // Dismisses a suggestion so an acknowledged problem stops resurfacing.
    public async Task MarkAttendedAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            using var _ = await _http.PostAsync(
                $"/api/suggestions/attend/{Uri.EscapeDataString(deviceId)}", null, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Dismissing is a convenience, not something worth surfacing a
            // failure for.
        }
    }

    private sealed class ErrorPayload
    {
        public List<string>? Errors { get; set; }
    }

    private sealed class FlushPayload
    {
        public int Moved { get; set; }
        public PipelineStatsDto? Stats { get; set; }
    }
}

// Outcome of a call that can fail for reasons the user needs to see.
public sealed class ApiResult
{
    public bool Succeeded { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static ApiResult Ok() => new() { Succeeded = true };

    public static ApiResult Fail(IReadOnlyList<string> errors) =>
        new() { Succeeded = false, Errors = errors };
}

// What the batch ingestion endpoint returns.
public sealed class BatchResultDto
{
    public int Accepted { get; set; }
    public List<string> Rejected { get; set; } = [];
    public PipelineStatsDto? Stats { get; set; }
}

// What the alert-injection endpoint returns.
public sealed class AlertTestResultDto
{
    public bool Queued { get; set; }
    public bool SuppressedAsDuplicate { get; set; }
    public StreamSnapshotDto? Snapshot { get; set; }
}

// What the device search endpoint returns.
public sealed class DeviceSearchResultDto
{
    public string Query { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<SensorProfile> Devices { get; set; } = [];
}