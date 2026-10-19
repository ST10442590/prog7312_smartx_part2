# Smart-X IoT Mesh Ecosystem — Part 2

Real-time processing and device management for the Smart-X hybrid IoT
ecosystem. Part 1 built the ingestion gateway; Part 2 turns it into a
processing engine with priority alert handling, manual device overrides with
undo, constant-time lookups, and a predictive engine that learns operator
behaviour.

**Module:** PROG7312 / AAPD7112 — Programming 3B / Advanced Application Development
**Student:** Thabang Thamae (ST10442590)

---

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 10.0 or later |
| Browser | Any modern browser with WebAssembly support |

Check your SDK:

```bash
dotnet --list-sdks
```

If no `10.x` entry appears, install the .NET 10 SDK from
<https://dotnet.microsoft.com/download>.

---

## Quick start

```bash
git clone https://github.com/ST10442590/prog7312_smartx_part2.git
cd prog7312_smartx_part2
dotnet restore
dotnet build
```

The API and the client are **separate processes** and both must be running.
Open two terminals.

**Terminal 1 — the gateway API:**

```bash
dotnet run --project SmartX.Api
```

Listens on `http://localhost:5145`. On startup it registers 24 simulated
ESP32 nodes and begins publishing telemetry every 800 ms.

**Terminal 2 — the dashboard client:**

```bash
dotnet run --project SmartX.Client
```

Listens on `http://localhost:5006` and opens a browser automatically.

Stop either with `Ctrl+C`.

> **Start the API first.** The client polls the gateway and will show
> "Gateway unreachable" until the API answers. It reconnects on its own once
> the API is up — no client restart needed.

> **Stop the API before rebuilding.** A running process holds a lock on
> `SmartX.Api.exe`, and the build cannot overwrite it.

---

## Testing the Part 2 data structures

Every structure can be exercised from the **Command Stream** page at
`http://localhost:5006/stream`, or directly against the API. The depth
counters at the top of that page update roughly once a second, so each
structure can be watched filling and draining.

### Priority queue — making a critical alert bypass the stream

The standard FIFO queue holds routine telemetry. Critical alerts skip it
entirely and are released first, in severity order.

**From the dashboard:** in the *Alert queue* panel, pick a device, set
severity to **Critical**, and click **Inject alert**. It appears at the top
of the queue regardless of what is already waiting.

**From the command line:**

```bash
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-A00&severity=Critical"
```

To see the ordering clearly, inject a low-severity alert first and a
critical one immediately after, then read the queue:

```bash
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-B01&severity=Informational"
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-A00&severity=Critical"
curl http://localhost:5145/api/stream/snapshot
```

`pendingAlerts` lists them in dequeue order: the Critical alert is first even
though it arrived second.

### HashSet — duplicate alert suppression

Inject the **same alert twice**. The first is queued; the second is refused,
because the active-alert set already holds that condition.

```bash
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-A00&severity=Critical"
curl -X POST "http://localhost:5145/api/stream/simulate-alert?deviceId=ESP32-A00&severity=Critical"
```

The first returns `"queued": true`; the second returns
`"suppressedAsDuplicate": true`. The `duplicatesSuppressed` counter on the
dashboard shows the running total — without this set, a node that drops out
for ninety seconds would raise the same alert on every poll.

### Stack — undoing manual overrides

In the *Manual override* panel, send three commands to different devices,
then click **Undo last** three times. The stack unwinds newest-first, and the
entry marked *next undo* is always the one that will be reverted.

```bash
curl -X POST http://localhost:5145/api/stream/command \
  -H "Content-Type: application/json" \
  -d "{\"deviceId\":\"ESP32-F05\",\"kind\":0,\"newState\":true}"

curl -X POST http://localhost:5145/api/stream/undo
```

Undo applies the *inverse* of the popped command rather than deleting
history, so the audit trail stays intact.

### Dictionary — constant-time MAC lookup

Devices are indexed by MAC address, so resolving an incoming packet does not
require scanning the device list.

```bash
curl http://localhost:5145/api/index/by-mac/5C:CF:7F:00:00:00
curl http://localhost:5145/api/index/by-category/PowerConsumption
curl http://localhost:5145/api/index/stats
```

`indexStats` reports hits, misses and rebuilds. The category index is a
second dictionary, so filtering by category is also a lookup rather than a
scan.

### SortedDictionary — ordered timeline

```bash
curl "http://localhost:5145/api/index/timeline?take=20"
curl "http://localhost:5145/api/index/timeline/range?fromSecondsAgo=60&toSecondsAgo=0"
```

Entries come back in chronological order with no sort performed at request
time — ordering is a property of the structure. The range query stops
scanning as soon as it passes the end of the range.

### Predictive engine — teaching it a pattern

The engine learns what you do after a given situation. Search two devices in
sequence, twice, then return to the first:

```bash
curl "http://localhost:5145/api/suggestions/search?query=ESP32-A00"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-B01"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-A00"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-B01"
curl "http://localhost:5145/api/suggestions/search?query=ESP32-A00"
curl http://localhost:5145/api/suggestions
```

A suggestion for ESP32-B01 appears, with the rationale *"After searched
ESP32-A00, you searched it 2 of the last 2 times."* The same sequence works
from the search box on the Command Stream page.

> The model is in-memory. Restarting the API clears it, which is useful for
> demonstrating it learning from nothing.

---

## Solution structure

```
SmartX/
├── SmartX.Shared/     Class library referenced by both other projects
│   ├── Telemetry/     TelemetryPacket<T>, SensorReading, health windows, DTOs
│   ├── Commands/      Commands, alerts, suggestion and stream DTOs
│   ├── Sensors/       Registration profiles, categories, units, health states
│   ├── Collections/   RingBuffer<T>, jagged-array batch buffer
│   └── Topology/      Deployment tree and recursive validator
├── SmartX.Api/        Minimal API gateway
│   └── Services/      Registry, simulator, archive, command stream,
│                      lookup index, suggestion engine, topology builder
└── SmartX.Client/     Blazor WebAssembly console
    ├── Components/    PulseTile
    ├── Pages/         Dashboard, registry, diagnostics, command stream
    └── Services/      Typed API client
```

Domain types live in `SmartX.Shared` so the same validation logic runs in the
browser and on the server. Registration errors a user sees while typing are
produced by the identical method the API runs on submission.

---

## Navigation

Four modules are active; the third architectural pillar is visible but
disabled so the intended architecture is legible from the first screen.

| Module | Route | Part |
|---|---|---|
| Sensor Data Ingestion | `/` | 1 |
| Sensor Registry | `/devices` | 1 |
| Pipeline Diagnostics | `/diagnostics` | 1 |
| Real-Time Command Stream | `/stream` | 2 |
| Network Topology and Mesh Routing | — | Final PoE |

Routing is client-side, so switching modules does not reload the application
or interrupt the polling loops. Each page disposes its own timer on
navigation, which is why moving between views does not leave orphaned
requests running against the API.

---

## API reference

### Part 2 — command stream

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/stream/snapshot` | Depths and contents of all four structures |
| `POST` | `/api/stream/command` | Issue a manual override |
| `POST` | `/api/stream/undo` | Revert the most recent command |
| `POST` | `/api/stream/simulate-alert` | Inject a test alert |

### Part 2 — lookups

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/index/stats` | Index and timeline counters |
| `GET` | `/api/index/by-mac/{mac}` | O(1) MAC resolution |
| `GET` | `/api/index/by-category/{category}` | Category index |
| `GET` | `/api/index/timeline` | Ordered timeline |
| `GET` | `/api/index/timeline/range` | Range query |

### Part 2 — suggestions

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/suggestions` | Recommendations and the learned model |
| `GET` | `/api/suggestions/search` | Device search, also a training signal |
| `POST` | `/api/suggestions/attend/{deviceId}` | Dismiss a suggestion |
| `POST` | `/api/suggestions/reset` | Clear the learned model |

### Part 1 — ingestion, devices, diagnostics

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/health` | Liveness probe |
| `POST` | `/api/telemetry` | Ingest one packet |
| `POST` | `/api/telemetry/batch` | Ingest a buffered batch |
| `GET` | `/api/devices` | List registered sensors |
| `POST` | `/api/devices` | Register or update a sensor |
| `POST` | `/api/devices/{id}/attachments` | Upload a config file, photo or log |
| `GET` | `/api/mesh/health` | Pulse Grid tiles, worst first |
| `GET` | `/api/mesh/summary` | Mesh-wide counters |
| `GET` | `/api/topology/validate` | Recursive validation report |
| `GET` | `/api/diagnostics/benchmark` | Insertion and search timings |

OpenAPI document (development only): `http://localhost:5145/openapi/v1.json`

---

## Design notes

### Why these structures

**`Queue<T>`** holds routine telemetry. Readings must be processed in arrival
order, because a packet overtaking an earlier one would corrupt the timeline
the archive's binary search depends on.

**`PriorityQueue<DeviceAlert, long>`** holds alerts. The priority is
`severity × 1,000,000 + sequence`: severity dominates, and the sequence
number breaks ties. Without that tiebreaker two Critical alerts could be
released in the wrong order relative to each other, since `PriorityQueue` is
not a stable sort.

**`Stack<DeviceCommand>`** holds the override history. Undo applies the
inverse of the popped command rather than deleting it, so the audit trail
survives.

**`HashSet<string>`** holds active alert conditions, keyed on
`device|severity|type`. `Add` returns false when the key is already present,
making the check and the insert one constant-time operation. Conditions clear
when a device recovers, so the set does not grow without bound and a node
that fails twice can alarm twice.

**`Dictionary<string, SensorProfile>`** resolves MAC addresses. Scanning a
device list is O(n) per packet, which at the thousands of nodes the brief
describes would be paid on every reading.

**`SortedDictionary<DateTimeOffset, TimelinePoint>`** holds the timeline.
Packets usually arrive in time order but not always — a delayed node can
report late, and a plain dictionary would leave that reading out of sequence.

### How the suggestion engine works

The model is a first-order Markov transition table:

```
Dictionary<string, Dictionary<string, int>>
   situation  ->   next action  ->  times observed
```

Three constraints keep it honest:

- Only **operator actions** can be the target of a learned transition. Two
  unrelated alerts firing in the same window is coincidence, not behaviour.
- Transitions must occur within **three minutes**. Something done ten minutes
  later is a separate task, not a response.
- A pattern must be seen **at least twice** before it is suggested.

Suggestions come from two sources. *Learned transitions* predict what the
operator usually does next. *Unattended anomalies* surface devices that are
degrading but have not been looked at — the proactive half.

Every suggestion carries its rationale, so a recommendation can be traced to
the observations that produced it. An operator who cannot see why a system is
recommending something will not act on it.

---

## Troubleshooting

**"Gateway unreachable" on the dashboard**
The API is not running, or is on a different port. Confirm the API terminal
shows `Now listening on: http://localhost:5145`.

**Build fails with "file is locked by SmartX.Api"**
The API is still running. Stop it with `Ctrl+C`, or
`taskkill /IM SmartX.Api.exe /F`.

**CORS errors in the browser console**
The API allows only `http://localhost:5006` and `https://localhost:7296`. If
your client started on a different port, update `WithOrigins` in
`SmartX.Api/Program.cs`.

**Suggestions panel is empty**
The model is in-memory and resets with the API. Run the training sequence
above, and check within five minutes — that is the context window.

**Port already in use**
Change `applicationUrl` in the relevant `Properties/launchSettings.json`, and
update the CORS origins if it was the client that moved.

---

## References

Ancker, J.S., Edwards, A., Nosal, S., Hauser, D., Mauer, E. & Kaushal, R.
2017. Effects of workload, work complexity, and repeated alerts on alert
fatigue in a clinical decision support system. *BMC Medical Informatics and
Decision Making*, 17(36):1–9.

Endsley, M.R. 1995. Toward a theory of situation awareness in dynamic
systems. *Human Factors*, 37(1):32–64.

Healey, C.G. & Enns, J.T. 2012. Attention and visual memory in visualization
and computer graphics. *IEEE Transactions on Visualization and Computer
Graphics*, 18(7):1170–1188.

Koivisto, J. & Hamari, J. 2019. The rise of motivational information systems:
a review of gamification research. *International Journal of Information
Management*, 45:191–210.

Stelea, G.A., Sangeorzan, L. & Enache-David, N. 2025. Accessible IoT
dashboard design with AI-enhanced descriptions for visually impaired users.
*Future Internet*, 17(7):274.

Zong, J., Lee, C., Lundgard, A., Jang, J., Hajas, D. & Satyanarayan, A. 2022.
Rich screen reader experiences for accessible data visualization. *Computer
Graphics Forum*, 41(3):15–27.
