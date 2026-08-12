using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.DeviceCert;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.DigiMine;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Libs;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;
using Google.Protobuf;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Web;

public sealed class TabletSimulatorWebHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TabletSimulatorContext _context;
    private readonly HttpListener _listener = new();
    private readonly List<StreamWriter> _sseWriters = [];
    private readonly object _sseLock = new();
    private CancellationTokenSource? _cts;

    public TabletSimulatorWebHost(TabletSimulatorContext context)
    {
        _context = context;
        _context.MqttClient.InboundReceived += OnInbound;
        _context.MqttClient.SessionChanged += OnSessionChanged;
        _context.MqttActivityLog.EntryAdded += OnMqttLogEntry;
    }

    public async Task RunAsync(int port, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        Console.WriteLine($"Web UI: http://localhost:{port}/");

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx), _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _context.MqttClient.InboundReceived -= OnInbound;
        _context.MqttClient.SessionChanged -= OnSessionChanged;
        _context.MqttActivityLog.EntryAdded -= OnMqttLogEntry;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        _listener.Stop();
        _listener.Close();
        lock (_sseLock)
        {
            foreach (var writer in _sseWriters)
            {
                writer.Dispose();
            }

            _sseWriters.Clear();
        }
    }

    private void OnInbound(object? sender, TabletInboundMessageEventArgs e) => BroadcastInbound(e.Message);

    private void OnSessionChanged(object? sender, EventArgs e) => BroadcastSseEvent("session", SerializeSession());

    private void OnMqttLogEntry(object? sender, MqttActivityLogEntry entry) =>
        BroadcastSseEvent("mqttLog", SerializeMqttLogEntry(entry));

    private void BroadcastInbound(TabletInboundMessage message)
    {
        // Unnamed SSE event keeps Home page `es.onmessage` working.
        // Omit payloadHex (can be huge during FULL sync) — UI loads it from SQLite on demand.
        BroadcastSseRaw($"data: {SerializeInboundLive(message)}\n\n");
    }

    private void BroadcastSseEvent(string eventName, string json) =>
        BroadcastSseRaw($"event: {eventName}\ndata: {json}\n\n");

    private void BroadcastSseRaw(string payload)
    {
        lock (_sseLock)
        {
            for (var i = _sseWriters.Count - 1; i >= 0; i--)
            {
                try
                {
                    var writer = _sseWriters[i];
                    writer.Write(payload);
                    writer.Flush();
                }
                catch (IOException)
                {
                    _sseWriters[i].Dispose();
                    _sseWriters.RemoveAt(i);
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext http)
    {
        try
        {
            var path = http.Request.Url?.AbsolutePath ?? "/";
            var method = http.Request.HttpMethod;

            if (path is "/" or "/devices.html")
            {
                await ServeFileAsync(http, "devices.html", "text/html").ConfigureAwait(false);
                return;
            }

            if (path is "/home.html" or "/index.html")
            {
                await ServeFileAsync(http, "index.html", "text/html").ConfigureAwait(false);
                return;
            }

            if (path == "/add-device.html")
            {
                await ServeFileAsync(http, "add-device.html", "text/html").ConfigureAwait(false);
                return;
            }

            if (path == "/settings.html")
            {
                await ServeFileAsync(http, "settings.html", "text/html").ConfigureAwait(false);
                return;
            }

            if (path == "/device-cert.html")
            {
                await ServeFileAsync(http, "device-cert.html", "text/html").ConfigureAwait(false);
                return;
            }

            if (path == "/common.css")
            {
                await ServeFileAsync(http, "common.css", "text/css").ConfigureAwait(false);
                return;
            }

            if (path == "/api/devices" && method == "GET")
            {
                _context.Config.EnsureDevicesMigrated();
                var session = _context.MqttClient.GetSessionSnapshot();
                await WriteJsonAsync(http, new
                {
                    activeDeviceId = _context.Config.Device.DeviceId,
                    mqttConnected = session.Connected,
                    mqttBroker = session.Broker,
                    mqttEnvironment = session.Environment,
                    autoDisposeEnabled = session.AutoDisposeEnabled,
                    autoDisposeMinutes = session.AutoDisposeMinutes,
                    autoDisposeAt = session.AutoDisposeAt?.ToString("O"),
                    devices = _context.Config.Devices.Select(d =>
                    {
                        var isActive = string.Equals(
                            d.DeviceId,
                            _context.Config.Device.DeviceId,
                            StringComparison.OrdinalIgnoreCase);
                        return new
                        {
                            d.DeviceId,
                            d.EquipmentId,
                            d.Name,
                            d.CertificateFolder,
                            isActive,
                            mqttConnected = isActive && session.Connected,
                            mqttBroker = isActive ? session.Broker : null,
                            mqttEnvironment = isActive ? session.Environment : null,
                            connectedAt = isActive ? session.ConnectedAt?.ToString("O") : null,
                            autoDisposeAt = isActive ? session.AutoDisposeAt?.ToString("O") : null,
                        };
                    }).ToArray(),
                }).ConfigureAwait(false);
                return;
            }

            if (path == "/api/devices" && method == "POST")
            {
                await HandleAddDeviceAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/devices" && method == "PUT")
            {
                await HandleUpdateDeviceAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/devices/select" && method == "POST")
            {
                await HandleSelectDeviceAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/config" && method == "GET")
            {
                await WriteJsonAsync(http, _context.Config).ConfigureAwait(false);
                return;
            }

            if (path == "/api/config" && method == "PUT")
            {
                await HandleSaveConfigAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/connect" && method == "POST")
            {
                await HandleConnectAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/disconnect" && method == "POST")
            {
                await HandleDisconnectAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/mqtt/sessions" && method == "GET")
            {
                await HandleMqttSessionsAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/mqtt/disconnect-all" && method == "POST")
            {
                await HandleMqttDisconnectAllAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/mqtt/auto-dispose" && method == "POST")
            {
                await HandleMqttAutoDisposeAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/mqtt/log" && method == "GET")
            {
                await HandleMqttLogAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/mqtt/log" && method == "DELETE")
            {
                _context.MqttActivityLog.Clear();
                await WriteJsonAsync(http, new { ok = true }).ConfigureAwait(false);
                return;
            }

            if (path == "/api/validate" && method == "POST")
            {
                await HandleValidateAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/certificates/upload" && method == "POST")
            {
                await HandleCertificateUploadAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/certificates/export-pfx" && method == "POST")
            {
                await HandleExportPfxAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/certificates/files" && method == "POST")
            {
                await HandleCertificateFilesAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/certificates/content" && method == "POST")
            {
                await HandleCertificateContentAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/device-cert/config" && method == "GET")
            {
                await HandleDeviceCertConfigAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/device-cert/generate" && method == "POST")
            {
                await HandleDeviceCertGenerateAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/device-cert/save-bundle" && method == "POST")
            {
                await HandleDeviceCertSaveBundleAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/libs/status" && method == "GET")
            {
                await HandleLibsStatusAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/libs/sync" && method == "POST")
            {
                await HandleLibsSyncAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/digimine/query" && method == "POST")
            {
                await HandleDigiMineQueryAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/status" && method == "GET")
            {
                var session = _context.MqttClient.GetSessionSnapshot();
                await WriteJsonAsync(http, new
                {
                    deviceId = session.DeviceId,
                    equipmentId = session.EquipmentId,
                    name = session.Name ?? string.Empty,
                    broker = session.Broker,
                    environment = session.Environment,
                    connected = session.Connected,
                    usesNodeBridge = session.UsesNodeBridge,
                    connectedAt = session.ConnectedAt?.ToString("O"),
                    autoDisposeEnabled = session.AutoDisposeEnabled,
                    autoDisposeMinutes = session.AutoDisposeMinutes,
                    autoDisposeAt = session.AutoDisposeAt?.ToString("O"),
                    subscriptions = session.Subscriptions,
                    subscriptionsReady = session.SubscriptionsReady,
                    confirmedSubscriptions = session.ConfirmedSubscriptions ?? session.Subscriptions,
                    uplinkTopics = TabletTopicCatalog.GetUplinkTopics(session.DeviceId),
                    defaultTopic = TabletTopicCatalog.ResolveUplinkKey(session.DeviceId, "sync"),
                    environments = _context.Config.Environments.Select(e => e.Name).ToArray(),
                }).ConfigureAwait(false);
                return;
            }

            if (path == "/api/events" && method == "GET")
            {
                await HandleSseAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/inbound" && method == "GET")
            {
                await HandleGetInboundAsync(http).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/api/inbound/", StringComparison.Ordinal) && method == "GET")
            {
                var sequenceRaw = path["/api/inbound/".Length..];
                if (long.TryParse(sequenceRaw, out var sequence))
                {
                    await HandleGetInboundBySequenceAsync(http, sequence).ConfigureAwait(false);
                    return;
                }
            }

            if (path == "/api/inbound" && method == "POST")
            {
                await HandleSyncInboundAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/inbound" && method == "DELETE")
            {
                await HandleClearInboundAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/outbound" && method == "GET")
            {
                await HandleGetOutboundAsync(http).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/api/outbound/", StringComparison.Ordinal) && method == "GET")
            {
                var sequenceRaw = path["/api/outbound/".Length..];
                if (long.TryParse(sequenceRaw, out var sequence))
                {
                    await HandleGetOutboundBySequenceAsync(http, sequence).ConfigureAwait(false);
                    return;
                }
            }

            if (path == "/api/outbound" && method == "DELETE")
            {
                await HandleClearOutboundAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/decode" && method == "POST")
            {
                await HandleDecodeAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/encode" && method == "POST")
            {
                await HandleEncodeAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/presets/sync-full" && method == "GET")
            {
                await HandleSyncFullPresetAsync(http).ConfigureAwait(false);
                return;
            }

            if (path == "/api/presets/task-event" && method == "GET")
            {
                await HandleTaskEventPresetAsync(http).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/api/storage/", StringComparison.Ordinal) && method == "GET")
            {
                await HandleGetStorageAsync(http, path["/api/storage/".Length..]).ConfigureAwait(false);
                return;
            }

            if (path == "/api/storage" && method == "PUT")
            {
                await HandlePutStorageAsync(http).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/api/storage/", StringComparison.Ordinal) && method == "DELETE")
            {
                await HandleDeleteStorageAsync(http, path["/api/storage/".Length..]).ConfigureAwait(false);
                return;
            }

            if (path == "/api/publish" && method == "POST")
            {
                await HandlePublishAsync(http).ConfigureAwait(false);
                return;
            }

            http.Response.StatusCode = 404;
            http.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task HandleSaveConfigAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<SimulatorConfig>(body, JsonOptions)
            ?? throw new InvalidOperationException("Invalid config payload.");

        var oldDeviceId = _context.Config.Device.DeviceId;
        _context.Config.ActiveEnvironment = config.ActiveEnvironment;
        _context.Config.Device = config.Device;
        _context.Config.Web = config.Web;
        _context.Config.DeviceCert = config.DeviceCert ?? new DeviceCertOptions();
        _context.Config.Libs = config.Libs ?? new LibsOptions();
        _context.Config.Devices.Clear();
        _context.Config.Devices.AddRange(config.Devices);
        _context.Config.Environments.Clear();
        _context.Config.Environments.AddRange(config.Environments);
        _context.Config.EnsureDevicesMigrated();
        _context.Config.SyncActiveDeviceEntry();
        // Persist per-device certificate folders from the saved device list.
        _context.Devices.ReplaceAll(_context.Config.Devices);
        _context.ConfigStore.Save(_context.Config);

        var deviceIdChanged = !string.Equals(
            oldDeviceId,
            _context.Config.Device.DeviceId,
            StringComparison.OrdinalIgnoreCase);
        var reconnected = false;
        if (_context.MqttClient.IsConnected)
        {
            // Reconnect so TLS uses the active device's certificate folder.
            await _context.MqttClient.ReconnectAsync(_context.Config.ActiveEnvironment).ConfigureAwait(false);
            reconnected = true;
        }

        await WriteJsonAsync(http, new { ok = true, reconnected, deviceIdChanged }).ConfigureAwait(false);
    }

    private async Task HandleAddDeviceAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<AddDeviceRequest>(body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "deviceId is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            _context.Config.EnsureDevicesMigrated();
            var entry = _context.Config.AddDevice(request.DeviceId, request.Name, request.EquipmentId);
            _context.Config.SelectDevice(entry.DeviceId);
            _context.Devices.Upsert(entry);
            _context.ConfigStore.Save(_context.Config);

            var reconnected = false;
            if (_context.MqttClient.IsConnected)
            {
                await _context.MqttClient.ReconnectAsync(_context.Config.ActiveEnvironment).ConfigureAwait(false);
                reconnected = true;
            }

            await WriteJsonAsync(http, new
            {
                ok = true,
                reconnected,
                deviceId = entry.DeviceId,
                equipmentId = entry.EquipmentId,
                name = entry.Name,
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleUpdateDeviceAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<UpdateDeviceRequest>(body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "deviceId is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            _context.Config.EnsureDevicesMigrated();
            var entry = _context.Config.UpdateDeviceName(request.DeviceId, request.Name);
            _context.Devices.Upsert(entry);
            _context.ConfigStore.Save(_context.Config);

            await WriteJsonAsync(http, new
            {
                ok = true,
                deviceId = entry.DeviceId,
                equipmentId = entry.EquipmentId,
                name = entry.Name,
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleSelectDeviceAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<SelectDeviceRequest>(body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "deviceId is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            _context.Config.EnsureDevicesMigrated();
            var oldDeviceId = _context.Config.Device.DeviceId;
            _context.Config.SelectDevice(request.DeviceId);
            _context.ConfigStore.Save(_context.Config);

            var reconnected = false;
            if (!string.Equals(oldDeviceId, _context.Config.Device.DeviceId, StringComparison.OrdinalIgnoreCase)
                && _context.MqttClient.IsConnected)
            {
                await _context.MqttClient.ReconnectAsync(_context.Config.ActiveEnvironment).ConfigureAwait(false);
                reconnected = true;
            }

            await WriteJsonAsync(http, new
            {
                ok = true,
                reconnected,
                deviceId = _context.Config.Device.DeviceId,
                equipmentId = _context.Config.Device.EquipmentId,
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<ConnectRequest>(body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.EnvironmentName))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "environmentName is required" }).ConfigureAwait(false);
            return;
        }

        var log = new ConnectionAttemptLog();
        try
        {
            if (request.SaveActive)
            {
                _context.Config.ActiveEnvironment = request.EnvironmentName;
                _context.ConfigStore.Save(_context.Config);
            }

            log.Info($"Connect requested for environment '{request.EnvironmentName}'");
            await _context.MqttClient.ReconnectAsync(request.EnvironmentName, log).ConfigureAwait(false);
            var env = _context.Config.GetActiveEnvironment();
            env.NormalizeHost();
            await WriteJsonAsync(http, new
            {
                ok = true,
                deviceId = _context.Config.Device.DeviceId,
                equipmentId = _context.Config.Device.EquipmentId,
                environment = env.Name,
                broker = env.GetBrokerUrl(),
                connected = _context.MqttClient.IsConnected,
                usesNodeBridge = _context.MqttClient.UsesNodeBridge,
                subscriptions = _context.MqttClient.ActiveSubscriptions,
                log = log.Entries,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message, log = log.Entries }).ConfigureAwait(false);
        }
    }

    private async Task HandleDisconnectAsync(HttpListenerContext http)
    {
        try
        {
            await _context.MqttClient.DisconnectAllAsync("manual disconnect").ConfigureAwait(false);
            await WriteJsonAsync(http, new
            {
                ok = true,
                connected = _context.MqttClient.IsConnected,
                deviceId = _context.Config.Device.DeviceId,
                equipmentId = _context.Config.Device.EquipmentId,
                session = _context.MqttClient.GetSessionSnapshot(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleMqttSessionsAsync(HttpListenerContext http)
    {
        var session = _context.MqttClient.GetSessionSnapshot();
        await WriteJsonAsync(http, new
        {
            sessions = new[] { session },
            connectedCount = session.Connected ? 1 : 0,
            autoDisposeEnabled = session.AutoDisposeEnabled,
            autoDisposeMinutes = session.AutoDisposeMinutes,
            autoDisposeAt = session.AutoDisposeAt?.ToString("O"),
        }).ConfigureAwait(false);
    }

    private async Task HandleMqttDisconnectAllAsync(HttpListenerContext http)
    {
        try
        {
            await _context.MqttClient.DisconnectAllAsync("disconnect-all from UI").ConfigureAwait(false);
            await WriteJsonAsync(http, new
            {
                ok = true,
                connected = false,
                session = _context.MqttClient.GetSessionSnapshot(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleMqttAutoDisposeAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = string.IsNullOrWhiteSpace(body)
            ? new MqttAutoDisposeRequest()
            : JsonSerializer.Deserialize<MqttAutoDisposeRequest>(body, JsonOptions) ?? new MqttAutoDisposeRequest();

        var minutes = request.Minutes is > 0 ? request.Minutes.Value : 60;
        _context.MqttClient.ConfigureAutoDispose(request.Enabled, minutes);
        var session = _context.MqttClient.GetSessionSnapshot();
        await WriteJsonAsync(http, new
        {
            ok = true,
            autoDisposeEnabled = session.AutoDisposeEnabled,
            autoDisposeMinutes = session.AutoDisposeMinutes,
            autoDisposeAt = session.AutoDisposeAt?.ToString("O"),
            session,
        }).ConfigureAwait(false);
    }

    private async Task HandleMqttLogAsync(HttpListenerContext http)
    {
        var limitText = http.Request.QueryString["limit"];
        var limit = 200;
        if (int.TryParse(limitText, out var parsed) && parsed > 0)
        {
            limit = Math.Min(parsed, 1000);
        }

        var entries = _context.MqttActivityLog.GetRecent(limit);
        await WriteJsonAsync(http, new
        {
            entries = entries.Select(SerializeMqttLogEntryObject).ToArray(),
        }).ConfigureAwait(false);
    }

    private async Task HandleValidateAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<ValidateConnectionRequest>(body, JsonOptions);
        if (request?.Environment is null)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "environment is required" }).ConfigureAwait(false);
            return;
        }

        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId)
            ? _context.Config.Device.DeviceId
            : request.DeviceId;

        var environment = request.Environment;
        if (string.IsNullOrWhiteSpace(environment.Certificates?.Folder))
        {
            environment = _context.Config.PrepareEnvironmentForDevice(environment, deviceId);
        }

        var result = await MqttConnectionProbe.ValidateAsync(environment, deviceId).ConfigureAwait(false);
        http.Response.StatusCode = result.Ok ? 200 : 400;
        await WriteJsonAsync(http, new
        {
            ok = result.Ok,
            error = result.Error,
            step = result.Step,
            broker = result.Broker,
            elapsedMs = result.ElapsedMs,
            log = result.Log,
        }).ConfigureAwait(false);
    }

    private async Task HandleCertificateUploadAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<CertificateUploadRequest>(body, JsonOptions);
        if (request is null
            || string.IsNullOrWhiteSpace(request.EnvironmentName)
            || string.IsNullOrWhiteSpace(request.Field)
            || string.IsNullOrWhiteSpace(request.ContentBase64))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "environmentName, field and contentBase64 are required" }).ConfigureAwait(false);
            return;
        }

        var bytes = Convert.FromBase64String(request.ContentBase64);
        var path = _context.ConfigStore.SaveUploadedCertificate(
            request.EnvironmentName,
            request.Field,
            request.FileName,
            bytes);

        var env = _context.Config.Environments.Find(e =>
            string.Equals(e.Name, request.EnvironmentName, StringComparison.OrdinalIgnoreCase));
        if (env is not null)
        {
            switch (request.Field)
            {
                case "caFile":
                    env.Certificates.CaFile = path;
                    break;
                case "clientCertificateFile":
                    env.Certificates.ClientCertificateFile = path;
                    if (path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
                    {
                        env.Certificates.ClientKeyFile = string.Empty;
                    }

                    break;
                case "clientKeyFile":
                    env.Certificates.ClientKeyFile = path;
                    break;
            }

            _context.ConfigStore.Save(_context.Config);
        }

        await WriteJsonAsync(http, new { ok = true, path }).ConfigureAwait(false);
    }

    private async Task HandleCertificateFilesAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<CertificateFolderRequest>(body, JsonOptions);
        if (request is null)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "Invalid request" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var folder = ResolveCertificateFolder(request);
            var files = CertificatePathHelper.ListFolderFiles(folder);
            await WriteJsonAsync(http, new
            {
                folder,
                files = files.Select(f => new
                {
                    f.FileName,
                    f.Label,
                    f.Path,
                    f.Exists,
                    f.SizeBytes,
                }).ToArray(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleCertificateContentAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<CertificateContentRequest>(body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "fileName is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var folder = ResolveCertificateFolder(request);
            var content = CertificatePathHelper.ReadFolderFile(folder, request.FileName.Trim());
            await WriteJsonAsync(http, new
            {
                folder,
                fileName = request.FileName.Trim(),
                content,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or UnauthorizedAccessException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private string? ResolveCertificateFolder(CertificateFolderRequest request)
    {
        var folder = request.Folder?.Trim();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            return folder;
        }

        if (!string.IsNullOrWhiteSpace(request.EnvironmentName))
        {
            var env = _context.Config.Environments.Find(e =>
                string.Equals(e.Name, request.EnvironmentName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Environment '{request.EnvironmentName}' not found.");

            var certificates = new CertificatePaths { Folder = env.Certificates.Folder };
            CertificatePathHelper.ResolveFromFolder(certificates);
            folder = certificates.Folder;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                return folder;
            }
        }

        var deviceFolder = _context.Config.ResolveCertificateFolderForDevice();
        if (!string.IsNullOrWhiteSpace(deviceFolder))
        {
            return deviceFolder;
        }

        throw new InvalidOperationException("Certificate folder is not set for the active device.");
    }

    private async Task HandleDeviceCertConfigAsync(HttpListenerContext http)
    {
        var env = _context.Config.GetActiveEnvironment();
        env.NormalizeHost();
        var mqttCertFolder = _context.Config.ResolveCertificateFolderForDevice();
        if (!string.IsNullOrWhiteSpace(mqttCertFolder))
        {
            var paths = new CertificatePaths { Folder = mqttCertFolder };
            CertificatePathHelper.ResolveFromFolder(paths);
            mqttCertFolder = paths.Folder;
        }

        var outputRoot = DeviceCertPathHelper.ResolveBaseFolder(
            _context.Config.DeviceCert.OutputFolder,
            mqttCertFolder);
        var deviceId = _context.Config.Device.DeviceId?.Trim() ?? string.Empty;
        var savePreview = string.IsNullOrWhiteSpace(deviceId)
            ? Path.Combine(outputRoot, "{deviceId}")
            : DeviceCertPathHelper.ResolveDeviceFolder(outputRoot, deviceId);

        await WriteJsonAsync(http, new
        {
            deviceId,
            dssEnrollBaseUrl = _context.Config.DeviceCert.DssEnrollBaseUrl,
            outputFolder = outputRoot,
            savePathPreview = savePreview,
            activeEnvironment = env.Name,
            certificateFolder = mqttCertFolder,
        }).ConfigureAwait(false);
    }

    private async Task HandleDeviceCertGenerateAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<DeviceCertGenerateRequest>(body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "deviceId is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = DeviceCertService.Generate(request.DeviceId, request.Algorithm);
            var enrollUrl = DeviceCertService.BuildEnrollUrl(
                _context.Config.DeviceCert.DssEnrollBaseUrl,
                result.DeviceId);
            await WriteJsonAsync(http, new
            {
                result.DeviceId,
                result.Algorithm,
                result.KeyAlgorithm,
                result.CsrPem,
                result.PrivateKeyPem,
                result.EnrollPayloadJson,
                enrollUrl,
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleDeviceCertSaveBundleAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<DeviceCertSaveBundleRequest>(body, JsonOptions);
        if (request is null
            || string.IsNullOrWhiteSpace(request.DeviceId)
            || string.IsNullOrWhiteSpace(request.PrivateKeyPem))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "deviceId and privateKeyPem are required" }).ConfigureAwait(false);
            return;
        }

        if (request.EnrollResponse.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "enrollResponse is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var outputFolder = ResolveDeviceCertOutputFolder(request);
            var result = DeviceCertBundleService.SaveBundle(
                outputFolder,
                request.DeviceId,
                request.PrivateKeyPem,
                request.EnrollResponse);

            var envName = string.IsNullOrWhiteSpace(request.EnvironmentName)
                ? _context.Config.ActiveEnvironment
                : request.EnvironmentName;
            var env = _context.Config.Environments.Find(e =>
                string.Equals(e.Name, envName, StringComparison.OrdinalIgnoreCase));

            // Bind cert bundle to this device (each device keeps its own client cert folder).
            var deviceEntry = _context.Config.FindDevice(result.DeviceId);
            if (deviceEntry is not null)
            {
                deviceEntry.CertificateFolder = result.OutputDir;
            }
            else if (string.Equals(
                         result.DeviceId,
                         _context.Config.Device.DeviceId,
                         StringComparison.OrdinalIgnoreCase))
            {
                // Device not in list yet — still record on active device after ensure.
                _context.Config.EnsureDevicesMigrated();
                var active = _context.Config.FindDevice(result.DeviceId);
                if (active is not null)
                {
                    active.CertificateFolder = result.OutputDir;
                }
            }

            // Keep env folder as fallback only when it was empty.
            if (env is not null && string.IsNullOrWhiteSpace(env.Certificates.Folder))
            {
                env.Certificates.Folder = result.OutputDir;
            }

            _context.Devices.ReplaceAll(_context.Config.Devices);
            _context.ConfigStore.Save(_context.Config);

            var equipmentId = _context.Config.FindDevice(result.DeviceId)?.EquipmentId
                ?? _context.Config.Device.EquipmentId
                ?? string.Empty;
            var registrationUrl = DeviceCertService.BuildRegistrationUrl(
                _context.Config.DeviceCert.DssEnrollBaseUrl,
                result.DeviceId);
            var registrationPayloadJson = string.IsNullOrWhiteSpace(equipmentId)
                ? string.Empty
                : DeviceCertService.BuildRegistrationPayloadJson(equipmentId);

            await WriteJsonAsync(http, new
            {
                ok = true,
                result.DeviceId,
                equipmentId,
                outputDir = result.OutputDir,
                files = result.Files,
                certKeyWarning = result.CertKeyWarning,
                pfxWarning = result.PfxWarning,
                registrationUrl,
                registrationPayloadJson,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or JsonException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleDigiMineQueryAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<DigiMineProxyQueryRequest>(body, JsonOptions)
            ?? new DigiMineProxyQueryRequest();

        var digiMine = _context.Config.DigiMine ?? new DigiMineOptions();
        var entity = (request.Entity ?? "Device").Trim();
        var target = !string.IsNullOrWhiteSpace(request.Target)
            ? request.Target.Trim()
            : entity.Equals("Equipment", StringComparison.OrdinalIgnoreCase)
                ? digiMine.EquipmentQueryTarget
                : digiMine.DeviceQueryTarget;

        try
        {
            var result = await DigiMineQueryClient.QueryAsync(
                new DigiMineQueryRequest
                {
                    BaseUrl = !string.IsNullOrWhiteSpace(request.BaseUrl)
                        ? request.BaseUrl!
                        : digiMine.ConfigurationBaseUrl,
                    BearerToken = request.BearerToken ?? string.Empty,
                    OperationalUnitId = !string.IsNullOrWhiteSpace(request.OperationalUnitId)
                        ? request.OperationalUnitId!
                        : digiMine.OperationalUnitId,
                    Target = target,
                    SearchText = request.SearchText ?? string.Empty,
                    PageNumber = request.PageNumber,
                    PageSize = request.PageSize,
                }).ConfigureAwait(false);

            await WriteJsonAsync(http, new
            {
                ok = true,
                entity,
                target,
                totalCount = result.TotalCount,
                pageCount = result.PageCount,
                items = result.Items.Select(i => new
                {
                    i.Id,
                    i.Name,
                    i.Subtitle,
                }).ToArray(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleLibsStatusAsync(HttpListenerContext http)
    {
        var status = DssLibSyncService.GetStatus(_context.Config.Libs?.DssRepoRoot);
        await WriteJsonAsync(http, status).ConfigureAwait(false);
    }

    private async Task HandleLibsSyncAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = string.IsNullOrWhiteSpace(body)
            ? new LibsSyncRequest()
            : JsonSerializer.Deserialize<LibsSyncRequest>(body, JsonOptions) ?? new LibsSyncRequest();

        var dssRoot = !string.IsNullOrWhiteSpace(request.DssRepoRoot)
            ? request.DssRepoRoot.Trim()
            : DssLibSyncService.ResolveDefaultDssRepoRoot(_context.Config.Libs?.DssRepoRoot);
        var configuration = string.IsNullOrWhiteSpace(request.Configuration)
            ? "Debug"
            : request.Configuration.Trim();

        try
        {
            var result = await DssLibSyncService.SyncAsync(dssRoot, configuration).ConfigureAwait(false);
            _context.Config.Libs ??= new LibsOptions();
            _context.Config.Libs.DssRepoRoot = result.DssRepoRoot;
            _context.ConfigStore.Save(_context.Config);
            await WriteJsonAsync(http, result).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private string ResolveDeviceCertOutputFolder(DeviceCertSaveBundleRequest request)
    {
        var deviceId = request.DeviceId.Trim();
        if (!DeviceCertService.IsValidDeviceId(deviceId))
        {
            throw new InvalidOperationException("Device ID must be a UUID.");
        }

        var explicitBase = request.OutputFolder?.Trim();
        string baseRoot;
        if (!string.IsNullOrWhiteSpace(explicitBase))
        {
            baseRoot = explicitBase;
        }
        else if (!string.IsNullOrWhiteSpace(_context.Config.DeviceCert.OutputFolder))
        {
            baseRoot = _context.Config.DeviceCert.OutputFolder.Trim();
        }
        else
        {
            var envName = string.IsNullOrWhiteSpace(request.EnvironmentName)
                ? _context.Config.ActiveEnvironment
                : request.EnvironmentName;
            var env = _context.Config.Environments.Find(e =>
                string.Equals(e.Name, envName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Environment '{envName}' not found.");

            baseRoot = DeviceCertPathHelper.ResolveBaseFolder(null, env.Certificates.Folder);
        }

        return DeviceCertPathHelper.ResolveDeviceFolder(baseRoot, deviceId);
    }

    private async Task HandleExportPfxAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<ExportPfxRequest>(body, JsonOptions);
        if (request is null)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "Invalid request" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var env = request.Environment;
            if (env is null)
            {
                if (string.IsNullOrWhiteSpace(request.EnvironmentName))
                {
                    http.Response.StatusCode = 400;
                    await WriteJsonAsync(http, new { error = "environment or environmentName is required" }).ConfigureAwait(false);
                    return;
                }

                env = _context.Config.Environments.Find(e =>
                    string.Equals(e.Name, request.EnvironmentName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Environment '{request.EnvironmentName}' not found.");
            }

            var log = new ConnectionAttemptLog();
            var result = ClientPkcs12Exporter.ExportFromEnvironment(
                env,
                _context.ConfigStore,
                request.PfxPassword,
                log);

            if (request.UpdateConfig)
            {
                var stored = _context.Config.Environments.Find(e =>
                    string.Equals(e.Name, env.Name, StringComparison.OrdinalIgnoreCase));
                if (stored is not null)
                {
                    stored.Certificates.ClientCertificateFile = result.PfxPath;
                    stored.Certificates.ClientKeyFile = string.Empty;
                    if (!string.IsNullOrEmpty(request.PfxPassword))
                    {
                        stored.Password = request.PfxPassword;
                    }

                    _context.ConfigStore.Save(_context.Config);
                }
            }

            await WriteJsonAsync(http, new
            {
                ok = true,
                path = result.PfxPath,
                clientCertificateFile = result.PfxPath,
                clientKeyFile = string.Empty,
                usedPassword = result.UsedPassword,
                log = result.Log,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleSseAsync(HttpListenerContext http)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.Add("Cache-Control", "no-cache");
        http.Response.Headers.Add("Connection", "keep-alive");

        var writer = new StreamWriter(http.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };
        lock (_sseLock)
        {
            _sseWriters.Add(writer);
        }

        await writer.WriteLineAsync(": connected").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        // Live-only for inbound + mqtt log (clients load history via REST). Seed current session.
        await writer.WriteLineAsync("event: session").ConfigureAwait(false);
        await writer.WriteLineAsync($"data: {SerializeSession()}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        try
        {
            while (_cts is not null && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(15000, _cts.Token).ConfigureAwait(false);
                // Keep ping under the same lock as BroadcastSseRaw to avoid interleaved/corrupt SSE.
                lock (_sseLock)
                {
                    writer.Write(": ping\n\n");
                    writer.Flush();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sseLock)
            {
                _sseWriters.Remove(writer);
            }

            await writer.DisposeAsync().ConfigureAwait(false);
            http.Response.Close();
        }
    }

    private async Task HandlePublishAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<PublishRequest>(body, JsonOptions);

        if (request is null || string.IsNullOrWhiteSpace(request.Topic))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "topic is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            TabletInboundMessage outbound;
            if (!string.IsNullOrWhiteSpace(request.Payload))
            {
                outbound = await _context.MqttClient.PublishRawAsync(request.Topic, request.Payload, request.Retain)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(request.Preset))
            {
                var preset = request.Preset.ToUpperInvariant() switch
                {
                    "SYNC" or "SYNC-FULL" => UplinkPreset.SyncFull,
                    "SYNC-CONFIG" => UplinkPreset.SyncConfig,
                    "HEARTBEAT" or "TELEMETRY" => UplinkPreset.Heartbeat,
                    "SOS" => UplinkPreset.Sos,
                    "TASKEVENT" => UplinkPreset.TaskEvent,
                    _ => throw new InvalidOperationException($"Unknown preset: {request.Preset}"),
                };
                outbound = await _context.MqttClient.PublishPresetAsync(preset).ConfigureAwait(false);
            }
            else
            {
                http.Response.StatusCode = 400;
                await WriteJsonAsync(http, new { error = "payload or preset is required" }).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(http, new
            {
                ok = true,
                outbound = ToOutboundDto(outbound),
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleGetOutboundAsync(HttpListenerContext http)
    {
        var limit = 2000;
        var limitRaw = http.Request.QueryString["limit"];
        if (!string.IsNullOrWhiteSpace(limitRaw)
            && int.TryParse(limitRaw, out var parsed)
            && parsed > 0)
        {
            limit = Math.Min(parsed, 5000);
        }

        await WriteJsonAsync(http, new
        {
            databasePath = _context.Database.DatabasePath,
            total = _context.OutboundMessages.Count(),
            messages = _context.OutboundMessages.GetRecent(limit).Select(ToOutboundDto).ToArray(),
        }).ConfigureAwait(false);
    }

    private async Task HandleGetOutboundBySequenceAsync(HttpListenerContext http, long sequence)
    {
        var message = _context.OutboundMessages.GetBySequence(sequence);
        if (message is null)
        {
            http.Response.StatusCode = 404;
            await WriteJsonAsync(http, new { error = "message not found" }).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(http, ToOutboundDto(message)).ConfigureAwait(false);
    }

    private async Task HandleClearOutboundAsync(HttpListenerContext http)
    {
        _context.OutboundMessages.Clear();
        await WriteJsonAsync(http, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleGetInboundAsync(HttpListenerContext http)
    {
        var limit = 2000;
        var limitRaw = http.Request.QueryString["limit"];
        if (!string.IsNullOrWhiteSpace(limitRaw)
            && int.TryParse(limitRaw, out var parsed)
            && parsed > 0)
        {
            limit = Math.Min(parsed, 5000);
        }

        await WriteJsonAsync(http, new
        {
            databasePath = _context.Database.DatabasePath,
            total = _context.InboundMessages.Count(),
            messages = _context.InboundMessages.GetRecent(limit).Select(ToInboundDto).ToArray(),
        }).ConfigureAwait(false);
    }

    private async Task HandleGetInboundBySequenceAsync(HttpListenerContext http, long sequence)
    {
        var message = _context.InboundMessages.GetBySequence(sequence);
        if (message is null)
        {
            http.Response.StatusCode = 404;
            await WriteJsonAsync(http, new { error = "message not found" }).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(http, ToInboundDto(message)).ConfigureAwait(false);
    }

    private async Task HandleSyncInboundAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<InboundSyncRequest>(body, JsonOptions);
        if (request?.Messages is null || request.Messages.Count == 0)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "messages array is required" }).ConfigureAwait(false);
            return;
        }

        var imported = _context.InboundMessages.ImportMany(request.Messages.Select(ToInboundMessage));
        await WriteJsonAsync(http, new
        {
            ok = true,
            imported,
            total = _context.InboundMessages.Count(),
        }).ConfigureAwait(false);
    }

    private async Task HandleClearInboundAsync(HttpListenerContext http)
    {
        _context.InboundMessages.Clear();
        await WriteJsonAsync(http, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleDecodeAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<DecodeRequest>(body, JsonOptions);
        if (request is null
            || string.IsNullOrWhiteSpace(request.Topic)
            || string.IsNullOrWhiteSpace(request.PayloadHex))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "topic and payloadHex are required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var hex = request.PayloadHex.Trim().Replace(" ", "", StringComparison.Ordinal);
            if (hex.Length % 2 != 0
                || !hex.All(c => Uri.IsHexDigit(c)))
            {
                http.Response.StatusCode = 400;
                await WriteJsonAsync(http, new { error = "payloadHex must be even-length hex" }).ConfigureAwait(false);
                return;
            }

            var payload = Convert.FromHexString(hex);
            var decoded = TabletMqttClient.DecodeInbound(request.Topic.Trim(), payload);
            await WriteJsonAsync(http, new
            {
                ok = true,
                topic = request.Topic.Trim(),
                decodedSummary = decoded.Summary,
                payloadJson = decoded.PayloadJson,
                eventType = decoded.EventType,
                equipmentId = decoded.EquipmentId,
                payloadLength = payload.Length,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleEncodeAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<EncodeRequest>(body, JsonOptions);
        if (request is null
            || string.IsNullOrWhiteSpace(request.Topic)
            || string.IsNullOrWhiteSpace(request.Json))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "topic and json are required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var encoded = TabletMqttClient.EncodeJsonPreview(request.Topic, request.Json);
            await WriteJsonAsync(http, new
            {
                ok = true,
                topic = encoded.Topic,
                messageType = encoded.MessageType,
                payloadHex = encoded.PayloadHex,
                payloadLength = encoded.PayloadByteLength,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException
            or System.Text.Json.JsonException or FileNotFoundException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleSyncFullPresetAsync(HttpListenerContext http)
    {
        try
        {
            var (topic, json, payloadHex, messageType) =
                TabletPayloadFactory.CreateSyncFullPreview(_context.Config.Device);
            await WriteJsonAsync(http, new
            {
                ok = true,
                topic,
                json,
                payloadHex,
                messageType,
                deviceId = _context.Config.Device.DeviceId,
                equipmentId = _context.Config.Device.EquipmentId,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidProtocolBufferException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleTaskEventPresetAsync(HttpListenerContext http)
    {
        try
        {
            var eventType = http.Request.QueryString["eventType"];
            var (topic, json, payloadHex, messageType, resolvedEventType) =
                TabletPayloadFactory.CreateTaskEventPreview(_context.Config.Device, eventType);
            await WriteJsonAsync(http, new
            {
                ok = true,
                topic,
                json,
                payloadHex,
                messageType,
                eventType = resolvedEventType,
                deviceId = _context.Config.Device.DeviceId,
                equipmentId = _context.Config.Device.EquipmentId,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidProtocolBufferException)
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleGetStorageAsync(HttpListenerContext http, string key)
    {
        key = Uri.UnescapeDataString(key);
        if (string.IsNullOrWhiteSpace(key))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "key is required" }).ConfigureAwait(false);
            return;
        }

        var value = _context.AppStorage.Get(key);
        if (value is null)
        {
            http.Response.StatusCode = 404;
            await WriteJsonAsync(http, new { error = "not found" }).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(http, new { key, value }).ConfigureAwait(false);
    }

    private async Task HandlePutStorageAsync(HttpListenerContext http)
    {
        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<AppStorageRequest>(body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.Key))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "key is required" }).ConfigureAwait(false);
            return;
        }

        _context.AppStorage.Set(request.Key, request.Value ?? string.Empty);
        await WriteJsonAsync(http, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleDeleteStorageAsync(HttpListenerContext http, string key)
    {
        key = Uri.UnescapeDataString(key);
        if (string.IsNullOrWhiteSpace(key))
        {
            http.Response.StatusCode = 400;
            await WriteJsonAsync(http, new { error = "key is required" }).ConfigureAwait(false);
            return;
        }

        _context.AppStorage.Delete(key);
        await WriteJsonAsync(http, new { ok = true }).ConfigureAwait(false);
    }

    private static TabletInboundMessage ToInboundMessage(InboundMessageDto dto) =>
        new(
            dto.Sequence,
            dto.ReceivedAt,
            dto.Topic,
            dto.PayloadLength,
            dto.Retained,
            dto.DecodedSummary,
            dto.PayloadHex,
            dto.EventType,
            dto.EquipmentId);

    private static string SerializeInbound(TabletInboundMessage message) =>
        JsonSerializer.Serialize(ToInboundDto(message), SseJsonOptions);

    private static string SerializeInboundLive(TabletInboundMessage message)
    {
        const int maxSummaryChars = 4000;
        var summary = message.DecodedSummary ?? string.Empty;
        if (summary.Length > maxSummaryChars)
        {
            summary = summary[..maxSummaryChars] + "\n…(truncated for live SSE; open View log for full decode)";
        }

        return JsonSerializer.Serialize(new
        {
            message.Sequence,
            receivedAt = message.ReceivedAt.ToString("O"),
            message.Topic,
            message.PayloadLength,
            message.Retained,
            decodedSummary = summary,
            // Hex stays in SQLite — fetch via GET /api/inbound/{sequence} when viewing encoded.
            payloadHex = string.Empty,
            eventType = message.EventType,
            equipmentId = message.EquipmentId,
        }, SseJsonOptions);
    }

    private string SerializeSession() =>
        JsonSerializer.Serialize(_context.MqttClient.GetSessionSnapshot(), SseJsonOptions);

    private static string SerializeMqttLogEntry(MqttActivityLogEntry entry) =>
        JsonSerializer.Serialize(SerializeMqttLogEntryObject(entry), SseJsonOptions);

    private static object SerializeMqttLogEntryObject(MqttActivityLogEntry entry) => new
    {
        at = entry.At.ToString("O"),
        level = entry.Level,
        message = entry.Message,
        deviceId = entry.DeviceId,
    };

    private static object ToInboundDto(TabletInboundMessage message) => new
    {
        message.Sequence,
        receivedAt = message.ReceivedAt.ToString("O"),
        message.Topic,
        message.PayloadLength,
        message.Retained,
        message.DecodedSummary,
        message.PayloadHex,
        eventType = message.EventType,
        equipmentId = message.EquipmentId,
    };

    private static object ToOutboundDto(TabletInboundMessage message) => new
    {
        message.Sequence,
        publishedAt = message.ReceivedAt.ToString("O"),
        receivedAt = message.ReceivedAt.ToString("O"),
        message.Topic,
        message.PayloadLength,
        message.Retained,
        message.DecodedSummary,
        message.PayloadHex,
        eventType = message.EventType,
        equipmentId = message.EquipmentId,
    };

    private static async Task<string> ReadBodyAsync(HttpListenerContext http)
    {
        using var reader = new StreamReader(http.Request.InputStream, http.Request.ContentEncoding);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task ServeFileAsync(HttpListenerContext http, string fileName, string contentType)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "www", fileName);
        if (!File.Exists(path))
        {
            http.Response.StatusCode = 404;
            http.Response.Close();
            return;
        }

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        http.Response.ContentType = contentType;
        http.Response.ContentLength64 = bytes.Length;
        await http.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        http.Response.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerContext http, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        http.Response.ContentType = "application/json";
        http.Response.ContentLength64 = bytes.Length;
        await http.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        http.Response.Close();
    }
}
