using System.Text.Json;
using Bedrock.DigiMine.DeviceSyncService.ProtoDecoder;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;
using Google.Protobuf;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using DssMqttFilters = Bedrock.DigiMine.DeviceSyncService.Domain.Constants.MqttSubscriptionFilters;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public sealed class TabletMqttClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultAutoDisposeAfter = TimeSpan.FromHours(1);

    private readonly SimulatorConfig _config;
    private readonly InboundMessageStore _inboundStore;
    private readonly OutboundMessageStore _outboundStore;
    private readonly MqttActivityLog _activityLog;
    private readonly IMqttClient _client;
    private readonly NodeMqttBridgeService _nodeBridge = new();
    private long _inboundSequence;
    private long _outboundSequence;
    private readonly object _connectLock = new();
    private readonly object _disposeTimerLock = new();
    private MqttEnvironment _activeEnvironment;
    private bool _nodeBridgeConnected;
    private NodeMqttListenerSession? _nodeListener;
    private DateTimeOffset? _connectedAt;
    private bool _autoDisposeEnabled = true;
    private TimeSpan _autoDisposeAfter = DefaultAutoDisposeAfter;
    private DateTimeOffset? _autoDisposeAt;
    private CancellationTokenSource? _autoDisposeCts;
    private int _autoDisposeGeneration;
    private const int MaxInboundMessages = 2000;
    private const int MaxOutboundMessages = 2000;

    public TabletMqttClient(
        SimulatorConfig config,
        InboundMessageStore inboundStore,
        OutboundMessageStore outboundStore,
        MqttActivityLog activityLog)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(inboundStore);
        ArgumentNullException.ThrowIfNull(outboundStore);
        ArgumentNullException.ThrowIfNull(activityLog);
        _config = config;
        _inboundStore = inboundStore;
        _outboundStore = outboundStore;
        _activityLog = activityLog;
        _inboundSequence = inboundStore.GetMaxSequence();
        _outboundSequence = outboundStore.GetMaxSequence();
        _activeEnvironment = config.PrepareEnvironmentForDevice(config.GetActiveEnvironment());
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public bool EchoToConsole { get; set; }
    public event EventHandler<TabletInboundMessageEventArgs>? InboundReceived;
    public event EventHandler? SessionChanged;
    public IReadOnlyList<string> ActiveSubscriptions { get; private set; } = [];
    public IReadOnlyCollection<TabletInboundMessage> RecentInbound => _inboundStore.GetRecent(MaxInboundMessages);
    public IReadOnlyCollection<TabletInboundMessage> RecentOutbound => _outboundStore.GetRecent(MaxOutboundMessages);
    public string ActiveEnvironmentName => _activeEnvironment.Name;
    public bool IsConnected => _nodeBridgeConnected || _client.IsConnected;
    public bool UsesNodeBridge => _nodeBridgeConnected;
    public DateTimeOffset? ConnectedAt => _connectedAt;
    public bool AutoDisposeEnabled => _autoDisposeEnabled;
    public TimeSpan AutoDisposeAfter => _autoDisposeAfter;
    public DateTimeOffset? AutoDisposeAt => _autoDisposeAt;
    public MqttActivityLog ActivityLog => _activityLog;

    public async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken = default)
    {
        lock (_connectLock)
        {
            _activeEnvironment = _config.PrepareEnvironmentForDevice(_config.GetActiveEnvironment());
        }

        var log = new ConnectionAttemptLog();
        try
        {
            await ConnectInternalAsync(cancellationToken, log).ConfigureAwait(false);
            await SubscribeInternalAsync(cancellationToken, log).ConfigureAwait(false);
            MarkConnected(log);
        }
        catch (Exception ex)
        {
            MirrorLog(log);
            _activityLog.Error($"Startup connect failed: {ex.Message}", _config.Device.DeviceId);
            ClearConnectedState();
            NotifySessionChanged();
            throw;
        }
    }

    public async Task ReconnectAsync(
        string environmentName,
        ConnectionAttemptLog? log = null,
        CancellationToken cancellationToken = default)
    {
        var env = _config.Environments.Find(e => string.Equals(e.Name, environmentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Environment '{environmentName}' not found.");

        _config.ActiveEnvironment = env.Name;
        lock (_connectLock)
        {
            _activeEnvironment = _config.PrepareEnvironmentForDevice(env);
        }

        log ??= new ConnectionAttemptLog();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(MqttConnectionProbe.DefaultTimeout);

        try
        {
            CancelAutoDisposeTimer();
            _nodeBridgeConnected = false;
            await UnsubscribeActiveAsync(timeoutCts.Token).ConfigureAwait(false);
            await StopNodeListenerAsync().ConfigureAwait(false);
            ActiveSubscriptions = [];
            if (_client.IsConnected)
            {
                log.Info("Disconnecting current session...");
                await _client.DisconnectAsync(cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            }

            await ConnectInternalAsync(timeoutCts.Token, log).ConfigureAwait(false);
            await SubscribeInternalAsync(timeoutCts.Token, log).ConfigureAwait(false);
            MarkConnected(log);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log.Error($"Connection timed out after {MqttConnectionProbe.DefaultTimeout.TotalSeconds:0} seconds");
            MirrorLog(log);
            ClearConnectedState();
            NotifySessionChanged();
            throw new InvalidOperationException(
                $"Connection timed out after {MqttConnectionProbe.DefaultTimeout.TotalSeconds:0} seconds");
        }
        catch (Exception ex)
        {
            MirrorLog(log);
            _activityLog.Error($"Connect failed: {ex.Message}", _config.Device.DeviceId);
            ClearConnectedState();
            NotifySessionChanged();
            throw;
        }
    }

    public MqttSessionSnapshot GetSessionSnapshot()
    {
        MqttEnvironment env;
        lock (_connectLock)
        {
            env = _activeEnvironment;
        }

        env.NormalizeHost();
        var entry = _config.FindDevice(_config.Device.DeviceId);
        return new MqttSessionSnapshot(
            _config.Device.DeviceId,
            string.IsNullOrWhiteSpace(entry?.Name) ? null : entry!.Name,
            _config.Device.EquipmentId,
            env.Name,
            env.GetBrokerUrl(),
            IsConnected,
            UsesNodeBridge,
            _connectedAt,
            _autoDisposeEnabled,
            (int)Math.Round(_autoDisposeAfter.TotalMinutes),
            IsConnected ? _autoDisposeAt : null,
            ActiveSubscriptions,
            _nodeListener?.SubscriptionsReady ?? (!UsesNodeBridge && IsConnected && ActiveSubscriptions.Count > 0),
            _nodeListener?.ConfirmedSubscriptions ?? ActiveSubscriptions);
    }

    public void ConfigureAutoDispose(bool enabled, int? minutes = null)
    {
        _autoDisposeEnabled = enabled;
        if (minutes is > 0)
        {
            _autoDisposeAfter = TimeSpan.FromMinutes(minutes.Value);
        }

        _activityLog.Info(
            enabled
                ? $"Auto-dispose enabled ({_autoDisposeAfter.TotalMinutes:0} min)"
                : "Auto-dispose disabled",
            _config.Device.DeviceId);

        if (IsConnected && enabled)
        {
            ScheduleAutoDispose();
        }
        else
        {
            CancelAutoDisposeTimer();
            if (!enabled)
            {
                _autoDisposeAt = null;
            }
        }

        NotifySessionChanged();
    }

    public async Task DisconnectAllAsync(string reason = "manual disconnect-all", CancellationToken cancellationToken = default)
    {
        _activityLog.Info($"Closing MQTT session: {reason}", _config.Device.DeviceId);
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TabletInboundMessage> PublishAsync(
        string uplinkTopic,
        byte[] payload,
        bool retain = false,
        CancellationToken cancellationToken = default)
    {
        if (_nodeBridgeConnected)
        {
            MqttEnvironment env;
            lock (_connectLock)
            {
                env = _activeEnvironment;
            }

            var clientId = string.IsNullOrWhiteSpace(env.ClientId) ? _config.Device.DeviceId : env.ClientId;
            if (_nodeListener is not null)
            {
                clientId = $"{clientId}-pub";
            }

            var result = await _nodeBridge.PublishAsync(env, clientId, uplinkTopic, payload, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Error ?? "Node bridge publish failed.");
            }

            return RecordOutbound(uplinkTopic, payload, retain);
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(uplinkTopic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        return RecordOutbound(uplinkTopic, payload, retain);
    }

    public async Task<TabletInboundMessage> PublishPresetAsync(UplinkPreset preset, CancellationToken cancellationToken = default)
    {
        var device = _config.Device;
        var (topic, payload) = preset switch
        {
            UplinkPreset.SyncFull => (TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromSync, device.DeviceId), TabletPayloadFactory.CreateSyncRequest(device)),
            UplinkPreset.SyncConfig => (TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromSync, device.DeviceId), TabletPayloadFactory.CreateSyncRequest(device, "CONFIG")),
            UplinkPreset.Heartbeat => (TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromTelemetry, device.DeviceId), TabletPayloadFactory.CreateHeartbeat()),
            UplinkPreset.Sos => (TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromSos, device.DeviceId), TabletPayloadFactory.CreateSos(device)),
            UplinkPreset.TaskEvent => (TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromEvents, device.DeviceId), TabletPayloadFactory.CreateTaskEvent(device)),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };

        return await PublishAsync(topic, payload, retain: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAckAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var topic = TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromAck, _config.Device.DeviceId);
        await PublishAsync(topic, TabletPayloadFactory.CreateAck(_config.Device, messageId), retain: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TabletInboundMessage> PublishRawAsync(
        string uplinkTopic,
        string filePathOrHexOrJson,
        bool retain = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePathOrHexOrJson);

        var input = filePathOrHexOrJson.Trim();
        if (LooksLikeJsonPayload(input))
        {
            if (string.IsNullOrWhiteSpace(uplinkTopic))
            {
                throw new InvalidOperationException("Topic is required when publishing JSON (encoded via ProtoDecoder).");
            }

            var (_, json, _, _) = MqttProtoEncoder.ResolveJsonInput(input);
            var encoded = MqttProtoEncoder.Encode(uplinkTopic, json);
            return await PublishAsync(uplinkTopic, encoded.WireBytes.ToArray(), retain, cancellationToken).ConfigureAwait(false);
        }

        var (resolvedTopic, bytes, _, topicAutoDetected, _) = MqttProtoDecoder.ResolveInput(input);
        var topic = topicAutoDetected ? resolvedTopic! : uplinkTopic;
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new InvalidOperationException("Topic is required when the input is not an MQTT PUBLISH capture.");
        }

        return await PublishAsync(topic, bytes, retain, cancellationToken).ConfigureAwait(false);
    }

    private static bool LooksLikeJsonPayload(string input)
    {
        var trimmed = input.Trim().Trim('"');
        if (trimmed.StartsWith('{')
            || trimmed.StartsWith('[')
            || trimmed.Contains("Decoded payload:", StringComparison.Ordinal))
        {
            return true;
        }

        // Saved decoder-output / JSON files — encode via ProtoDecoder.
        if ((trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            && File.Exists(trimmed))
        {
            var content = File.ReadAllText(trimmed).TrimStart();
            return content.StartsWith('{')
                || content.StartsWith('[')
                || content.Contains("Decoded payload:", StringComparison.Ordinal);
        }

        return false;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelAutoDisposeTimer();
        var wasConnected = IsConnected;
        await UnsubscribeActiveAsync(cancellationToken).ConfigureAwait(false);
        await StopNodeListenerAsync().ConfigureAwait(false);
        _nodeBridgeConnected = false;
        ActiveSubscriptions = [];
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        ClearConnectedState();
        if (wasConnected)
        {
            _activityLog.Info("MQTT disconnected", _config.Device.DeviceId);
        }

        NotifySessionChanged();
    }

    public async ValueTask DisposeAsync()
    {
        CancelAutoDisposeTimer();
        await DisconnectAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    private void MarkConnected(ConnectionAttemptLog log)
    {
        MirrorLog(log);
        _connectedAt = DateTimeOffset.UtcNow;
        _activityLog.Info(
            $"MQTT connected to {_activeEnvironment.GetBrokerUrl()}"
            + (UsesNodeBridge ? " (Node bridge)" : string.Empty),
            _config.Device.DeviceId);
        if (_autoDisposeEnabled)
        {
            ScheduleAutoDispose();
        }
        else
        {
            _autoDisposeAt = null;
        }

        NotifySessionChanged();
    }

    private void MirrorLog(ConnectionAttemptLog log)
    {
        foreach (var entry in log.Entries)
        {
            var level = entry.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ? "error" : "info";
            _activityLog.Add(level, entry, _config.Device.DeviceId);
        }
    }

    private void ClearConnectedState()
    {
        _connectedAt = null;
        _autoDisposeAt = null;
    }

    private void ScheduleAutoDispose()
    {
        CancelAutoDisposeTimer();
        if (!_autoDisposeEnabled || _autoDisposeAfter <= TimeSpan.Zero)
        {
            _autoDisposeAt = null;
            return;
        }

        _autoDisposeAt = DateTimeOffset.UtcNow.Add(_autoDisposeAfter);
        var generation = Interlocked.Increment(ref _autoDisposeGeneration);
        var cts = new CancellationTokenSource();
        lock (_disposeTimerLock)
        {
            _autoDisposeCts = cts;
        }

        _activityLog.Info(
            $"Auto-dispose scheduled at {_autoDisposeAt:HH:mm:ss} UTC ({_autoDisposeAfter.TotalMinutes:0} min)",
            _config.Device.DeviceId);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_autoDisposeAfter, cts.Token).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _autoDisposeGeneration))
                {
                    return;
                }

                await DisconnectAllAsync(
                    $"auto-dispose after {_autoDisposeAfter.TotalMinutes:0} minutes",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _activityLog.Error($"Auto-dispose failed: {ex.Message}", _config.Device.DeviceId);
            }
        });
    }

    private void CancelAutoDisposeTimer()
    {
        lock (_disposeTimerLock)
        {
            if (_autoDisposeCts is null)
            {
                return;
            }

            try
            {
                _autoDisposeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _autoDisposeCts.Dispose();
            _autoDisposeCts = null;
        }
    }

    private void NotifySessionChanged() => SessionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Best-effort unsubscribe of current downlink filters so the broker does not keep a stale session.
    /// Node bridge path unsubscribes inside mqtt-bridge.js on stop.
    /// </summary>
    private async Task UnsubscribeActiveAsync(CancellationToken cancellationToken)
    {
        if (_nodeBridgeConnected || !_client.IsConnected || ActiveSubscriptions.Count == 0)
        {
            return;
        }

        try
        {
            var options = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var topic in ActiveSubscriptions)
            {
                options.WithTopicFilter(topic);
            }

            await _client.UnsubscribeAsync(options.Build(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort — disconnect still runs after this.
        }
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken, ConnectionAttemptLog? log = null)
    {
        MqttEnvironment env;
        lock (_connectLock)
        {
            env = _activeEnvironment;
        }

        var clientId = string.IsNullOrWhiteSpace(env.ClientId) ? _config.Device.DeviceId : env.ClientId;
        log?.Info($"Connecting to {env.GetBrokerUrl()} as {clientId}...");

        if (NodeMqttBridgeService.ShouldUseNodeBridge(env))
        {
            await ConnectViaNodeBridgeAsync(env, clientId, log, cancellationToken).ConfigureAwait(false);
            return;
        }

        MqttConnectionProbe.ValidateCertificates(env, log ?? new ConnectionAttemptLog());

        var options = MqttConnectionProbe.BuildOptions(env, clientId, log);
        var connectResult = await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
        {
            log?.Error($"MQTT CONNACK: {connectResult.ResultCode}");
            throw new InvalidOperationException($"MQTT connect failed: {connectResult.ResultCode}");
        }

        log?.Info($"MQTT CONNACK: {connectResult.ResultCode}");
    }

    private async Task ConnectViaNodeBridgeAsync(
        MqttEnvironment env,
        string clientId,
        ConnectionAttemptLog? log,
        CancellationToken cancellationToken)
    {
        log?.Info("Using Node.js OpenSSL MQTT bridge (same as MQTTX)");

        await StopNodeListenerAsync().ConfigureAwait(false);

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var filters = TabletTopicCatalog.GetDownlinkSubscriptionFilters(_config.Device.DeviceId);
        var listenerClientId = $"{clientId}-listen";
        log?.Info(
            $"Starting Node listener as {listenerClientId} for {filters.Count} topic filter(s): {string.Join(", ", filters)}");

        var listener = await _nodeBridge.StartListenerAsync(
                env,
                listenerClientId,
                filters,
                onMessage: OnNodeListenerMessage,
                onLog: (_, line) =>
                {
                    log?.Info(line);
                    // Also keep persistent global activity log after connect attempt ends.
                    _activityLog.Info(line, _config.Device.DeviceId);
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _nodeListener = listener;
        _nodeBridgeConnected = true;
        ActiveSubscriptions = filters;
        log?.Info(
            listener.SubscriptionsReady
                ? $"Connected via Node bridge — subscriptions ready: {string.Join(", ", listener.ConfirmedSubscriptions.Count > 0 ? listener.ConfirmedSubscriptions : filters)}"
                : $"Connected via Node bridge — listening for downlink on: {string.Join(", ", filters)}");
    }

    private void OnNodeListenerMessage(object? sender, TabletInboundMessageEventArgs e) =>
        RecordInbound(e.Message);

    private async Task StopNodeListenerAsync()
    {
        if (_nodeListener is null)
        {
            return;
        }

        _nodeListener.MessageReceived -= OnNodeListenerMessage;
        await _nodeListener.DisposeAsync().ConfigureAwait(false);
        _nodeListener = null;
    }

    private async Task SubscribeInternalAsync(CancellationToken cancellationToken, ConnectionAttemptLog? log = null)
    {
        if (_nodeBridgeConnected)
        {
            var confirmed = _nodeListener?.ConfirmedSubscriptions ?? [];
            if (confirmed.Count > 0)
            {
                foreach (var filter in confirmed)
                {
                    log?.Info($"Broker confirmed subscribe: {filter}");
                }
            }
            else
            {
                foreach (var filter in ActiveSubscriptions)
                {
                    log?.Info($"Subscribed (Node bridge, awaiting confirm): {filter}");
                }
            }

            return;
        }

        var subscriptionFilters = TabletTopicCatalog.GetDownlinkSubscriptionFilters(_config.Device.DeviceId);
        var subscribed = new List<string>(subscriptionFilters.Count);
        foreach (var filter in subscriptionFilters)
        {
            await _client.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic(filter)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                cancellationToken).ConfigureAwait(false);
            subscribed.Add(filter);
            log?.Info($"Subscribed: {filter}");
        }

        ActiveSubscriptions = subscribed;
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var payload = args.ApplicationMessage.PayloadSegment.ToArray();
        var decoded = DecodeInbound(topic, payload);
        var inbound = new TabletInboundMessage(
            0,
            DateTimeOffset.UtcNow,
            topic,
            payload.Length,
            args.ApplicationMessage.Retain,
            decoded.Summary,
            payload.Length > 0 ? Convert.ToHexString(payload) : string.Empty,
            decoded.EventType,
            decoded.EquipmentId);

        RecordInbound(inbound);
        return Task.CompletedTask;
    }

    private void RecordInbound(TabletInboundMessage inbound)
    {
        var sequenced = inbound with { Sequence = Interlocked.Increment(ref _inboundSequence) };
        _inboundStore.Save(sequenced);
        _activityLog.Info(
            $"Inbound MQTT #{sequenced.Sequence}: {sequenced.Topic} ({sequenced.PayloadLength} bytes, retain={sequenced.Retained})",
            _config.Device.DeviceId);
        InboundReceived?.Invoke(this, new TabletInboundMessageEventArgs(sequenced));
    }

    private TabletInboundMessage RecordOutbound(string topic, byte[] payload, bool retain)
    {
        var decoded = DecodeInbound(topic, payload);
        var outbound = new TabletInboundMessage(
            0,
            DateTimeOffset.UtcNow,
            topic,
            payload.Length,
            retain,
            decoded.Summary,
            payload.Length > 0 ? Convert.ToHexString(payload) : string.Empty,
            decoded.EventType,
            decoded.EquipmentId);
        var sequenced = outbound with { Sequence = Interlocked.Increment(ref _outboundSequence) };
        _outboundStore.Save(sequenced);
        _activityLog.Info(
            $"Outbound MQTT #{sequenced.Sequence}: {sequenced.Topic} ({sequenced.PayloadLength} bytes, retain={sequenced.Retained})",
            _config.Device.DeviceId);
        return sequenced;
    }

    internal static string DecodeInboundSummary(string topic, byte[] payload) =>
        DecodeInbound(topic, payload).Summary;

    internal static InboundDecodeResult DecodeInbound(string topic, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return new InboundDecodeResult("(empty payload)", null);
        }

        try
        {
            var descriptor = TopicMessageRouter.Resolve(topic);
            var decodeInner = InnerPayloadSupport.SupportsInnerPayload(descriptor.MessageName);
            var result = MqttProtoDecoder.Decode(topic, payload, "mqtt");
            var fields = TryExtractPayloadFields(result, decodeInner);
            var payloadJson = MqttProtoDecoder.FormatPayloadJson(result.Root, result.WireBytes, decodeInner);
            return new InboundDecodeResult(
                MqttProtoDecoder.FormatOutput(result, decodeInner),
                fields.EventType,
                fields.EquipmentId,
                payloadJson);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return new InboundDecodeResult($"Decode failed: {ex.Message}", null, null);
        }
        catch (InvalidOperationException ex)
        {
            return new InboundDecodeResult($"Decode failed: {ex.Message}", null, null);
        }
    }

    internal static EncodePreviewResult EncodeJsonPreview(string topic, string json)
    {
        var (_, normalizedJson, _, _) = MqttProtoEncoder.ResolveJsonInput(json);
        var encoded = MqttProtoEncoder.Encode(topic.Trim(), normalizedJson);
        return new EncodePreviewResult(
            encoded.Topic,
            encoded.MessageType,
            Convert.ToHexString(encoded.WireBytes.Span),
            encoded.PayloadByteLength);
    }

    private static (string? EventType, string? EquipmentId) TryExtractPayloadFields(
        DecodeResult result,
        bool decodeInnerPayload)
    {
        try
        {
            var json = MqttProtoDecoder.FormatPayloadJson(result.Root, result.WireBytes, decodeInnerPayload);
            using var document = JsonDocument.Parse(json);
            return (
                ReadJsonStringProperty(document.RootElement, "eventType"),
                ReadJsonStringProperty(document.RootElement, "equipmentId"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadJsonStringProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString(),
        };
    }
}

internal sealed record InboundDecodeResult(
    string Summary,
    string? EventType,
    string? EquipmentId = null,
    string? PayloadJson = null);

internal sealed record EncodePreviewResult(
    string Topic,
    string MessageType,
    string PayloadHex,
    int PayloadByteLength);

public enum UplinkPreset
{
    SyncFull,
    SyncConfig,
    Heartbeat,
    Sos,
    TaskEvent,
}
