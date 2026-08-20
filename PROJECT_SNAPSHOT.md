# PROJECT SNAPSHOT — bedrock.digimine.tablet-simulator
> Generated: 2026-06-26  
> Purpose: Standalone MQTT tablet simulator with web UI for testing DeviceSyncService (DSS) MQTT flows.  
> Related repo: `bedrock.digimine.devicesyncservice` (sibling folder; source of shared DLLs)
---
## 1. Executive Summary
| Item | Value |
|------|-------|
| **Name** | Bedrock DigiMine Tablet Simulator |
| **Solution** | `Bedrock.DigiMine.TabletSimulator.sln` |
| **Main project** | `src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator/` |
| **Target** | .NET 8 console app (`net8.0`) |
| **Web UI** | Static HTML + `HttpListener` on `http://localhost:{port}` (default **5055**) |
| **MQTT client** | MQTTnet 4.x (plain/TLS via Schannel) + optional Node.js OpenSSL bridge for `mqtts://` on Windows |
| **Shared libs** | DLL references from `lib/` (Domain + ProtoDecoder), synced from DSS repo |
| **NuGet (private)** | `BGT.DigiMine.Grpc.Shared` 1.0.148 (Azure DevOps feed) |
**What it simulates:** A mining tablet device publishing protobuf uplink messages and receiving downlink messages over MQTT, matching DSS topic conventions and payload formats.
---
## 2. Repository Layout
bedrock.digimine.tablet-simulator/ ├── Bedrock.DigiMine.TabletSimulator.sln ├── Directory.Build.props # net8.0, nullable, implicit usings ├── Directory.Packages.props # Central package versions ├── nuget.config # nuget.org + IRHTechnology Azure feed ├── README.md ├── scripts/ │ └── sync-libs.ps1 # Build + copy DSS Domain + ProtoDecoder DLLs → lib/ ├── lib/ # Gitignored DLLs; XML docs may be present │ ├── Bedrock.DigiMine.DeviceSyncService.Domain.dll (+ .xml) │ └── Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.dll (+ .xml) └── src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator/ ├── Program.cs ├── TabletSimulatorDependencyInjection.cs ├── simulator-config.json # Runtime config (copied to output) ├── Configuration/ │ ├── SimulatorConfig.cs │ └── SimulatorConfigStore.cs ├── Mqtt/ # MQTT client, TLS, Node bridge, topics, payloads ├── Web/ │ ├── TabletSimulatorWebHost.cs │ └── ApiModels.cs ├── www/ # Static web UI │ ├── index.html │ ├── settings.html │ └── common.css └── NodeBridge/ # Node.js mqtts bridge ├── mqtt-bridge.js ├── package.json # mqtt ^5.10.4, node >=18 └── README.md

**Gitignored:** `bin/`, `obj/`, `.vs/`, `lib/*.dll`, `**/node_modules/`
---
## 3. Prerequisites & Setup
### Requirements
- .NET 8 SDK
- Node.js 18+ (required for `mqtts://` on Windows via OpenSSL bridge)
- Azure DevOps NuGet auth for `BGT.DigiMine.Grpc.Shared`
- Local clone of `bedrock.digimine.devicesyncservice` (sibling of this repo)
### First-time setup
```powershell
cd bedrock.digimine.tablet-simulator
powershell -File scripts/sync-libs.ps1
cd src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator/NodeBridge
npm install
cd ../../../..
dotnet build
Run
dotnet run --project src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator
# Web UI: http://localhost:5055
Refresh DLLs after DSS changes
powershell -File scripts/sync-libs.ps1
dotnet build
# Or use Settings → DSS / ProtoDecoder libs → Import decoder + gRPC version
# Optional custom DSS path:
powershell -File scripts/sync-libs.ps1 -DssRepoRoot "C:\path\to\bedrock.digimine.devicesyncservice"
sync-libs.ps1 behavior
Builds Bedrock.DigiMine.DeviceSyncService.Domain (src/.../Domain)
Builds Bedrock.DigiMine.DeviceSyncService.ProtoDecoder (tools/.../ProtoDecoder)
Copies .dll + .xml to lib/
Reads ProtoDecoder.deps.json and updates Directory.Packages.props `BGT.DigiMine.Grpc.Shared` version to match
Default DSS root: ../../bedrock.digimine.devicesyncservice relative to scripts/
4. NuGet & DLL Dependencies
NuGet packages (Directory.Packages.props)
Package	Version
BGT.DigiMine.Grpc.Shared
1.0.148
Google.Protobuf
3.31.1
MQTTnet
4.3.7.1207
DLL references (lib/, via sync-libs.ps1)
DLL	Used for
Bedrock.DigiMine.DeviceSyncService.Domain
MqttSubscriptionFilters constants, domain types
Bedrock.DigiMine.DeviceSyncService.ProtoDecoder
MqttProtoDecoder (decode), MqttProtoEncoder (JSON→wire encode), TopicMessageRouter, protobuf format
Protobuf types (from BGT.DigiMine.Grpc.Shared)
Used in TabletPayloadFactory.cs:

Bedrock.DigiMine.Protos.OT.SyncRequest, SyncPayload
DeviceHeartbeatPayload
SosEvent
AckMessage, AckStatus
EventEnvelope, EventType
Build fails with clear error if lib/*.dll missing (EnsureLibDlls MSBuild target).

5. Configuration (simulator-config.json)
Location at runtime: {AppContext.BaseDirectory}/simulator-config.json
Certificates upload root: {AppContext.BaseDirectory}/certificates/

Schema (camelCase JSON)
{
  "activeEnvironment": "LOCAL",
  "device": {
    "deviceId": "<guid>",
    "equipmentId": "<guid>"
  },
  "devices": [
    { "deviceId": "<guid>", "equipmentId": "<guid>", "name": "<optional label>", "certificateFolder": "<path to device certs>" }
  ],
  "deviceCert": {
    "dssEnrollBaseUrl": "https://localhost:5004/api/v1",
    "outputFolder": ""
  },
  "libs": {
    "dssRepoRoot": "C:\\Work\\IRH_Solutions\\bedrock.digimine.devicesyncservice",
    "syncOnStartup": true
  },
  "digiMine": {
    "configurationBaseUrl": "https://digimineconfigurationdev1.irh.ae",
    "operationalUnitId": "dcf5b0e5-5489-4020-81b3-6377e1d66034",
    "deviceQueryTarget": "/configurations?type=object&source=device&categoryId=bgt.mining.devices",
    "equipmentQueryTarget": "/configurations?type=object&source=equipment&categoryId=bgt.mining.equipments"
  },
  "web": {
    "port": 5055,
    "useConsole": false
  },
  "environments": [
    {
      "name": "LOCAL",
      "host": "localhost",
      "port": 1883,
      "clientId": "",
      "username": "dss",
      "password": "",
      "sslTls": false,
      "sslSecure": false,
      "alpn": "",
      "certificateType": "CA",
      "cleanSession": false,
      "keepAliveSeconds": 60,
      "useNodeMqttBridge": false,
      "certificates": {
        "folder": "",
        "caFile": "",
        "clientCertificateFile": "",
        "clientKeyFile": ""
      }
    }
  ]
}
Key config rules
Host normalization: mqtts://host:port in host field auto-parses host/port/sslTls
Client ID: defaults to device.deviceId if clientId empty
Certificate folder (MQTTX-style): folder with ca.crt, client.crt, client.key
On save: if certificates.folder set, individual file paths are cleared
Node bridge: auto-enabled on Windows when sslTls=true (or explicit useNodeMqttBridge: true)
CLI overrides (Program.cs)
Arg	Effect
--device-id <id>
Override device ID
--equipment-id <id>
Override equipment ID
--env <name>
Active environment
--web-port <port>
Web UI port
--console
Sets web.useConsole = true
6. Architecture & Runtime Flow
Program.Main
  ├─ SimulatorConfigStore.LoadFromArgs()
  ├─ TabletSimulatorDependencyInjection.Create()
  │    └─ new TabletMqttClient(config)
  ├─ MqttClient.ConnectAndSubscribeAsync()  [best-effort; failures logged]
  ├─ TabletSimulatorWebHost.RunAsync(port)
  └─ On shutdown: MqttClient.DisconnectAsync()
MQTT connection strategy
Plain MQTT (sslTls=false): MQTTnet direct TCP
TLS on non-Windows or Schannel path: MQTTnet + MqttTlsConfigurator (TLS 1.2, optional mTLS)
TLS on Windows (default): NodeMqttBridgeService — spawns node mqtt-bridge.js --stdin
Listener client: {clientId}-listen (subscribes downlink topics)
Publish client: {clientId}-pub (one-shot publish per message)
Why Node bridge: Windows Schannel often fails mTLS with PEM certs (0x80090304). Node/OpenSSL matches MQTTX behavior.

Inbound message pipeline
MQTT message received (MQTTnet or Node listener)
  → TabletInboundMessage(sequence, topic, payload, decodedSummary, payloadHex)
  → ConcurrentQueue (max 500)
  → InboundReceived event
  → Web SSE broadcast to /api/events clients
  → Decode via ProtoDecoder: TopicMessageRouter + MqttProtoDecoder.Decode()
Each inbound message gets a monotonic server `sequence` id. The web UI lists every publish (MQTTX-like) and dedupes only by `sequence` when merging SSE replay, `/api/inbound`, and localStorage — not by topic/payload/timestamp.
7. MQTT Topics
Uplink (device → service)
Resolved via TabletTopicCatalog.ResolveUplinkTopic(filter, deviceId) — replaces + in DSS filter with deviceId.

Filters from Bedrock.DigiMine.DeviceSyncService.Domain.Constants.MqttSubscriptionFilters:

Constant	UI preset key
SubFromSync
SYNC, SYNC-FULL, SYNC-CONFIG, SYNC-STATE, SYNC-TASK, SYNC-TUM-LIST, SYNC-TUM-STATE
SubFromEvents
TASKEVENT, EVENTS
SubFromSos
SOS
SubFromTelemetry
HEARTBEAT, TELEMETRY
SubFromAck
ACK
SubFromFilesUrlReq
FILES, FILESURL
Exact filter strings (e.g. from/+/sync) live in the Domain DLL — not duplicated in this repo.

Downlink (service → device) — subscriptions
$"to/{deviceId}/#"
"config/#"

Uplink (device → service) — also subscribed (so Outbound grid can listen live)
$"from/{deviceId}/#"

Received `from/…` messages are recorded as Outbound (not Inbound). Own publishes are recorded on publish and echo-deduped when the Node `-listen` client sees them again.

Default publish topic on home page
TabletTopicCatalog.ResolveUplinkKey(deviceId, "sync") → sync uplink topic

8. Uplink Payload Presets (TabletPayloadFactory)
Preset	Protobuf	Notes
SyncFull
SyncRequest
Type = "FULL" (CONFIG → STATE → TASK → TUM_LIST)
SyncConfig
SyncRequest
Type = "CONFIG"
SyncState
SyncRequest
Type = "STATE"
SyncTask
SyncRequest
Type = "TASK" (generated shiftId)
SyncTumList
SyncRequest
Type = "TUM_LIST" (generated shiftId)
SyncTumState
SyncRequest
Type = "TUM_STATE" (generated shiftId)
Heartbeat
DeviceHeartbeatPayload
battery=85, network=wifi, appVersion=tablet-simulator
Sos
SosEvent
deviceId, equipmentId, messageId, timestamp
TaskEvent
EventEnvelope + inner XxxPayload
default TaskCreated (adHoc=true) with sample task fields; also WorkplaceChecklistSubmitted (taskId + workplaceId required); CreateTaskEventPreview decodes nested payload JSON
Ack
AckMessage
via PublishAckAsync(messageId)
All envelopes include: messageId, deviceId, equipmentId, timestamps, version="1", priority=1. Task presets also set taskId/shiftId/operatorId/workplaceId where applicable.

9. REST API (TabletSimulatorWebHost)
Base: http://localhost:{port}

Method	Path	Description
GET
/, /devices.html
Device listing (landing)
GET
/home.html, /index.html
Simulator home (MQTT connect, inbound, publish)
GET
/add-device.html
Add device — DigiMine search pickers + manual GUID fallback
GET
/settings.html
Settings UI
GET
/common.css
Styles
GET
/api/devices
List registered devices + activeDeviceId
POST
/api/devices
{ deviceId, name?, equipmentId? } — add device, select as active; equipmentId optional (auto-generated if omitted); persists to SQLite `devices` + config JSON
PUT
/api/devices
{ deviceId, name? } — update device name/details in SQLite + config
POST
/api/devices/select
{ deviceId } — set active device; reconnects MQTT if connected
GET
/api/config
Full SimulatorConfig JSON
PUT
/api/config
Save config; reconnects if deviceId changed
GET
/api/status
deviceId, broker, connected, subscriptions, uplink topics
POST
/api/connect
{ environmentName, saveActive? } — reconnect MQTT
POST
/api/disconnect
Disconnect MQTT
GET
/api/mqtt/sessions
Active MQTT session snapshot(s) + auto-dispose status
POST
/api/mqtt/disconnect-all
Close MQTT session (unsubscribe + disconnect + stop Node listener)
POST
/api/mqtt/auto-dispose
`{ enabled, minutes? }` — default 60 minutes; schedules dispose after connect
GET
/api/mqtt/log
Recent global MQTT lifecycle log (`?limit=200`)
DELETE
/api/mqtt/log
Clear in-memory MQTT activity log
POST
/api/validate
{ environment, deviceId? } — probe without switching session
POST
/api/digimine/query
Proxy DigiMine Configuration `Common/query` — `{ bearerToken, entity?: Device|Equipment, target?, searchText?, pageNumber?, pageSize?, baseUrl?, operationalUnitId? }` → `{ items[{id,name,subtitle}], totalCount, pageCount }` (token not stored)
GET
/api/libs/status
lib/ DLL timestamps, DSS repo path, pinned vs detected `BGT.DigiMine.Grpc.Shared` version
POST
/api/libs/sync
{ dssRepoRoot?, configuration? } — build Domain+ProtoDecoder, copy to `lib/`, bump Grpc.Shared in `Directory.Packages.props` (rebuild+restart required)
POST
/api/certificates/upload
Base64 cert upload per environment
POST
/api/certificates/export-pfx
PEM → PFX for Windows Schannel
POST
/api/decode
{ topic, payloadHex } — re-decode with current ProtoDecoder / Grpc.Shared (used by inbound “Decoded log” view); also returns `payloadJson` for editor sync
POST
/api/encode
{ topic, json } — encode protobuf JSON via `MqttProtoEncoder` → `{ payloadHex, messageType, payloadLength }` (live hex preview)
GET
/api/presets/sync
SyncRequest for active device: `?type=FULL|CONFIG|STATE|TASK|TUM_LIST|TUM_STATE` (default FULL) → `{ topic, json, payloadHex, messageType, syncType, deviceId, equipmentId }`. `/api/presets/sync-full` is an alias.
GET
/api/presets/task-event
Default task EventEnvelope for active device: `?eventType=TaskCreated` (or TaskAssigned, WorkplaceChecklistSubmitted, etc.) → `{ topic, json, payloadHex, messageType, eventType, deviceId, equipmentId }` (JSON includes nested inner payload)
GET
/api/inbound
Recent inbound messages (server memory/SQLite; default limit 2000)
GET
/api/inbound/{sequence}
Full inbound message including `payloadHex` (used when live SSE omitted hex)
GET
/api/outbound
Recent outbound (uplink) publishes (SQLite; default limit 2000)
GET
/api/outbound/{sequence}
Full outbound message including `payloadHex`
DELETE
/api/outbound
Clear outbound history
GET
/api/events
SSE stream: unnamed = inbound live messages (summary truncated, no hex); `event: outbound` for uplink (device→service) live rows; `event: mqttLog` / `event: session` for lifecycle (no inbound history dump on connect)
POST
/api/publish
{ topic, payload? } or { topic, preset? } — payload may be hex, file, or JSON (JSON encoded via MqttProtoEncoder); response includes `{ ok, outbound }` row metadata
Publish presets (API)
SYNC, SYNC-FULL, SYNC-CONFIG, SYNC-STATE, SYNC-TASK, SYNC-TUM-LIST, SYNC-TUM-STATE, HEARTBEAT, TELEMETRY, SOS, TASKEVENT

10. Key Source Files
File	Responsibility
Program.cs
Entry point, connect, web host, graceful shutdown
TabletSimulatorDependencyInjection.cs
Wires TabletSimulatorContext
Configuration/SimulatorConfig.cs
Config models, broker URL helpers
Configuration/SimulatorConfigStore.cs
Load/save JSON, cert uploads, CLI parsing
Mqtt/TabletMqttClient.cs
Core MQTT: connect, subscribe, publish, inbound queue, auto-dispose timer
Mqtt/MqttActivityLog.cs
In-memory global MQTT lifecycle log (ring buffer)
Mqtt/MqttSessionSnapshot.cs
Session DTO for status / Devices panel
Mqtt/TabletTopicCatalog.cs
Topic resolution (uplink/downlink)
Mqtt/TabletPayloadFactory.cs
Protobuf preset builders
Mqtt/MqttConnectionProbe.cs
Validate connection (MQTTnet or Node)
Mqtt/MqttTlsConfigurator.cs
TLS 1.2, mTLS, CA validation (strict/permissive)
Mqtt/ClientCertificateLoader.cs
PEM/PFX loading, Windows Schannel prep
Mqtt/SchannelClientCertificateBootstrap.cs
Import CA/client cert to Windows cert store
Mqtt/NodeMqttBridgeService.cs
Spawn Node for validate/publish/listen
Mqtt/NodeMqttListenerSession.cs
Long-running Node listener, JSON event parsing
Mqtt/CertificatePathHelper.cs
MQTTX folder layout (ca.crt, client.crt, client.key)
Libs/DssLibSyncService.cs
Build/copy Domain+ProtoDecoder to lib/; bump Grpc.Shared in Directory.Packages.props
Mqtt/ClientPkcs12Exporter.cs
Export PEM → PFX
Persistence/SimulatorDatabase.cs
SQLite `simulator.db` (inbound_messages, outbound_messages, app_storage, devices)
Persistence/InboundMessageStore.cs
Inbound downlink CRUD in SQLite
Persistence/OutboundMessageStore.cs
Outbound uplink publish history in SQLite
Persistence/DeviceStore.cs
Device list CRUD in SQLite; sync with config on startup
Web/TabletSimulatorWebHost.cs
HttpListener + all API routes + SSE
NodeBridge/mqtt-bridge.js
OpenSSL MQTT: validate, publish, listen actions
11. Node.js MQTT Bridge
When used
NodeMqttBridgeService.ShouldUseNodeBridge(env) → sslTls && (useNodeMqttBridge || IsWindows())

Actions (stdin JSON)
Action	Purpose
validate
Connect + disconnect (connection test)
publish
Connect, publish QoS 1, disconnect
listen
Persistent connection; stdout JSON events
Listen event types
connected, subscribed, message, error, offline, closed, reconnecting

Requirements
PEM only for Node bridge (not PFX)
npm install in source NodeBridge/ (copied to output on build if node_modules exists)
Optional env: NODE_BINARY for custom node path
12. Web UI Features
Devices (devices.html) — landing at /
List saved devices (SQLite `devices` table + mirrored in simulator-config.json)
MQTT sessions panel (connected badge, broker, auto-close time) + Close all MQTT
Global MQTT log (live via SSE `mqttLog`)
Add device (device ID + optional name/details; equipment ID auto-generated)
Edit name — Edit name on Devices row updates SQLite + config
Select & open → Home for that device
Home (home.html / index.html)
Device/equipment ID display
Environment selector + Connect/Disconnect/Close all MQTT + auto-dispose (default 1 hour)
Global MQTT log + last connection attempt panel
Inbound table (service → device): SSE live updates, search, event type column, view decoded log / hex payload, export JSON, clear; visible cap 500 rows; after Sync FULL polls `/api/inbound` for ~25s so burst replies are not missed; light localStorage (no hex)
Times shown in IST (Asia/Kolkata). View log **Show times** converts payload unix epochs (`timestamp`, `eventTime`, etc.) to UTC + IST above the JSON
Publish section (Sync | Task tabs): Sync tab — SyncRequest JSON editor + Sync FULL / CONFIG / STATE / TASK / TUM_LIST / TUM_STATE (DSS order CONFIG → STATE → TASK → TUM_LIST) + Heartbeat; Task tab — event-type dropdown + EventEnvelope JSON editor (nested payload) + Load preset / Publish; both auto-encode hex via `/api/encode`
Outbound table (device → service): same columns as Inbound; populated on successful publish **and** when MQTT uplink on `from/{deviceId}/#` is received (live via SSE `event: outbound`); SQLite `outbound_messages` + light localStorage; search / refresh / export / clear / view log+hex
Settings (settings.html)
Multi-environment CRUD (add/delete)
Active device ID (read-only; managed on Devices page) / Equipment ID editable
MQTTX-style broker URL builder (mqtt/mqtts, host, port)
SSL/TLS, SSL secure (strict validation), Node bridge toggle
Certificate folder path (per active device: `devices[].certificateFolder`; env folder is fallback)
DSS / ProtoDecoder libs: import Domain+ProtoDecoder into `lib/` and align Grpc.Shared package version
Validate connection (does not change active session)
Save all settings
13. TLS / Certificate Handling
MQTTX-style folder
{certificates.folder}/
  ca.crt
  client.crt
  client.key
Windows Schannel path (MQTTnet)
Load PEM/PFX via ClientCertificateLoader
SchannelClientCertificateBootstrap imports CA to Root store, client cert to Personal store
MqttTlsConfigurator applies TLS 1.2, optional ALPN
sslSecure=false → permissive broker cert validation (MQTTX-style)
sslSecure=true → strict CA chain validation
Timeouts
MqttConnectionProbe.DefaultTimeout: 20s
NodeMqttBridgeService.DefaultTimeout: 25s
TabletMqttClient.ReconnectAsync: uses probe timeout
Web connect button: 30s client-side abort
14. Timeouts, Limits & Error Handling
Limit	Value
Max inbound messages (server SQLite)
5000
Max inbound messages (browser localStorage)
2000
Visible inbound rows (Home)
500
Connection timeout
20–25s
SSE keepalive ping
every 15s
SSE dead-client writes
Caught (`HttpListenerException` / `IOException` / `ObjectDisposedException`); writer removed — does not crash process
Auto-dispose MQTT
1 hour after connect (default; configurable; disable via UI)
Startup MQTT connect failure is non-fatal — app still launches web UI with message to fix settings.

15. Build Details (csproj)
Copies simulator-config.json, www/**, NodeBridge/package.json, mqtt-bridge.js, README.md to output
CopyNodeBridgeModules target copies node_modules to output after build
EnsureLibDlls fails build if Domain/ProtoDecoder DLLs missing
16. Common Issues & Fixes
Problem	Fix
Build: missing lib DLLs
Run scripts/sync-libs.ps1
mqtts:// fails on Windows (Schannel)
Enable Node MQTT bridge in settings
Node bridge: mqtt package not found
cd NodeBridge && npm install then rebuild
Cert errors
Use MQTTX folder layout; validate in Settings
NuGet restore fails
Authenticate to Azure DevOps IRHTechnology feed
BGT.DigiMine.Grpc.Shared mismatch
Align version in Directory.Packages.props with DSS repo
HttpListenerException 1229 / crash on MQTT log SSE
Fixed: closed EventSource clients are dropped instead of crashing; rebuild/restart simulator
17. Integration Context
This simulator is a standalone tool extracted from / alongside DeviceSyncService. It does NOT host DSS APIs or Kafka — it only:

Connects to the same MQTT broker DSS uses
Publishes tablet-format protobuf uplink messages
Subscribes to downlink topics DSS would publish to
Decodes inbound protobuf using shared ProtoDecoder
Sibling repo dependency: bedrock.digimine.devicesyncservice for Domain + ProtoDecoder source builds.

18. File Count Summary
Area	Files
C# source (excl. obj)
~22 files
Web UI
3 files (HTML/CSS)
Node bridge
3 files (+ node_modules)
Config/scripts
simulator-config.json, sync-libs.ps1, nuget.config, props, sln
No unit tests in this repository.

19. Quick Reference Commands
# Build
dotnet build
# Run
dotnet run --project src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator
# Run with overrides
dotnet run --project src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator -- --env DEV --device-id <guid>
# Skip startup lib sync
dotnet run --project src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator -- --skip-lib-sync
# Sync libs
powershell -File scripts/sync-libs.ps1
# Node setup
cd src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator/NodeBridge; npm install
20. Suggested Future-Chat Prompt Prefix
Context: bedrock.digimine.tablet-simulator — .NET 8 MQTT tablet simulator with web UI (port 5055).
Uses MQTTnet + optional Node OpenSSL bridge for mqtts on Windows.
Shared DLLs from lib/ (DSS Domain + ProtoDecoder). See PROJECT_SNAPSHOT.md.




> **Maintenance:** Whenever code, config, APIs, dependencies, or project structure change, update this snapshot on the same day. Append a dated entry under [Changelog](#changelog) with the change date/time and a short description of what was added or modified.



---

## Changelog

| Date & time (UTC) | Change |
|-------------------|--------|
| 2026-08-19 07:45 UTC | Task tab: **WorkplaceChecklistSubmitted** preset (same EventEnvelope path as other task events). |
| 2026-08-18 10:43 UTC | Home Sync tab: **Sync TUM_LIST** and **Sync TUM_STATE**; buttons ordered CONFIG → STATE → TASK → TUM_LIST (plus FULL / TUM_STATE). |
| 2026-08-18 10:40 UTC | Home Sync tab: added **Sync STATE**, **Sync CONFIG**, and **Sync TASK** beside Sync FULL (`GET /api/presets/sync?type=`). |
| 2026-08-17 14:50 UTC | View log **Show times** converts payload unix epoch fields to UTC + IST above the decoded JSON. |
| 2026-08-13 05:27 UTC | Outbound grid sorts newest-first (latest at top), matching Inbound. |
| 2026-08-12 13:45 UTC | Outbound grid now **listens** to uplink `from/{deviceId}/#` (plus existing downlink subscriptions). Device-side TaskCreated / Sync / etc. appear live via SSE `event: outbound`; own publishes echo-deduped for Node bridge. |
| 2026-08-12 13:35 UTC | SSE broadcast no longer crashes the process when a browser tab closes: catch `HttpListenerException` / `ObjectDisposedException` (not only `IOException`) in `BroadcastSseRaw` and the `/api/events` keepalive ping, then drop dead writers. |
| 2026-08-11 | Home Publish: Sync \| Task tabs; Task tab edits EventEnvelope JSON (nested payload) via `/api/presets/task-event` + encode/publish. Outbound (device → service) grid mirrors Inbound columns; SQLite `outbound_messages`; `GET/DELETE /api/outbound`; publish response includes `outbound` row. Rich TaskCreated (ad-hoc) presets in TabletPayloadFactory. |
| 2026-07-29 | Inbound table: Refresh button reloads all from SQLite; fixed incremental-render race that could show only one row; lighter localStorage cache (truncated summaries). |
| 2026-07-29 | Fixed Node bridge clientId collision: listen/publish used same `environment.ClientId`, so Sync FULL publish kicked the listener and downlink never arrived. Handlers now attach before subscribe-ready; Global MQTT log shows each inbound receipt; UI shows subscription READY + confirmed topics. |
| 2026-07-29 | FULL sync downlink hardening: wait for all MQTT subscriptions before connect-ready; ordered Node inbound queue so bursts are not dropped; slim live SSE (no hex); post–Sync FULL inbound refresh for 25s; visible inbound rows raised to 500; `GET /api/inbound/{sequence}` for full payload. |
| 2026-07-29 | Sync FULL button again publishes after loading JSON/hex; clarified Inbound is downlink-only (uplink Sync FULL will not appear in that table). |
| 2026-07-28 | Home publish: Sync FULL JSON editor (max 400px) with live JSON↔hex via ProtoDecoder; `POST /api/encode`, `GET /api/presets/sync-full`; `/api/decode` returns `payloadJson`. Sync FULL loads editable preset (device/equipment IDs) instead of immediate publish. |
| 2026-07-28 | Global MQTT log + session panel; Close all MQTT; 1h auto-dispose (configurable); Home inbound UI perf (incremental rows, debounced light localStorage, no SSE history dump); APIs `/api/mqtt/sessions`, `/api/mqtt/log`, `/api/mqtt/disconnect-all`, `/api/mqtt/auto-dispose`; SSE `mqttLog`/`session` events. |
| 2026-07-20 | Disconnect cleanup: unsubscribe before MQTT disconnect; Node listener stop via stdin `{"cmd":"stop"}` (unsubscribe + `client.end`) before process kill; Home notes browser close does not drop MQTT. |
| 2026-07-19 | DigiMine Configuration picker: Settings `digiMine.*`; proxy `POST /api/digimine/query`; Add device searches devices/equipment with pasted Bearer token (sessionStorage only); optional `equipmentId` on `POST /api/devices`. |
| 2026-07-17 | Certificate folder is per-device (`devices[].certificateFolder` / SQLite); MQTT connect uses active device certs; Settings cert path binds to active device. |
| 2026-07-17 | Inbound grid: Equipment column from payload `equipmentId` (decode + SQLite `equipment_id`; shows — when absent). |
| 2026-07-16 | Inbound “Decoded log” re-decodes from `payloadHex` via `POST /api/decode` so fields like `deadlineHours` appear after Grpc/ProtoDecoder upgrades (stored summary can be stale). |
| 2026-07-16 | On startup, best-effort run `scripts/sync-libs.ps1` (`libs.syncOnStartup`, default true; `--skip-lib-sync` to bypass). |
| 2026-07-16 | Settings: Import ProtoDecoder/Domain libs from DSS repo (`GET/POST /api/libs/*`); sync-libs.ps1 also bumps `BGT.DigiMine.Grpc.Shared` from ProtoDecoder.deps.json. |
| 2026-07-16 | Device cert: after Save cert files, show copyable Postman registration URL (`…/api/v1.0/devices/{deviceId}/registration`) and `{ equipmentId }` body. |
| 2026-07-14 | Devices persisted in SQLite `devices` table (`device_id`, `equipment_id`, `name`); mirrored to config JSON. PUT `/api/devices` to update name; Devices screen has Edit name. |
| 2026-07-14 | Synced Domain + ProtoDecoder from DSS (`MqttProtoEncoder` included); bumped `BGT.DigiMine.Grpc.Shared` 1.0.136 → 1.0.148; publish accepts JSON via encoder; sync-libs falls back to obj when ProtoDecoder.bin is locked. |
| 2026-07-08 | Optional `name` field on device entries: add-device form, devices list, settings, and Home status; stored in `simulator-config.json` `devices[].name`. |
| 2026-06-26 | Device cert page: CSR generation, compact Postman JSON copy, paste enroll response, save `ca.crt`/`client.crt`/`client.key`/`client.pfx`; after save, copyable equipment registration URL/body for Postman. |
| 2026-06-26 | Device listing landing page (`/`), add-device screen, multi-device list in `simulator-config.json`; Home at `/home.html`. |
| 2026-06-26 | Inbound table shows `eventType` and `equipmentId` columns when present in decoded payload (e.g. EventEnvelope / taskevents). |
| 2026-06-26 | Inbound listing is MQTTX-like: server assigns monotonic `sequence` per message; UI dedupes only by `sequence` (replay merge), not topic/payload/timestamp. |
| 2026-06-26 — initial | Created PROJECT_SNAPSHOT.md with full project overview, architecture, MQTT topics, API routes, setup, and key file map. |

### How to update

When you change the project:

1. Edit the relevant sections in this file (not only the changelog).
2. Add a new row to the table above with **date, time, and details** of what changed.
3. Keep entries newest-first if you prefer, or oldest-first — stay consistent.

**Example entry:**

| 2026-06-27 14:30 UTC | Added `/api/foo` endpoint in `TabletSimulatorWebHost.cs`; documented in §9 REST API. |


Keep PROJECT_SNAPSHOT.md in sync: on any project change, update the snapshot and log the change with date/time in its Changelog section.