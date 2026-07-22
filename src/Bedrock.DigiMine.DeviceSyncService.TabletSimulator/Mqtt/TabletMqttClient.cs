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
    private readonly SimulatorConfig _config;
    private readonly InboundMessageStore _inboundStore;
    private readonly IMqttClient _client;
    private readonly NodeMqttBridgeService _nodeBridge = new();
    private long _inboundSequence;
    private readonly object _connectLock = new();
    private MqttEnvironment _activeEnvironment;
    private bool _nodeBridgeConnected;
    private NodeMqttListenerSession? _nodeListener;
    private const int MaxInboundMessages = 500;

    public TabletMqttClient(SimulatorConfig config, InboundMessageStore inboundStore)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(inboundStore);
        _config = config;
        _inboundStore = inboundStore;
        _inboundSequence = inboundStore.GetMaxSequence();
        _activeEnvironment = config.PrepareEnvironmentForDevice(config.GetActiveEnvironment());
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public bool EchoToConsole { get; set; }
    public event EventHandler<TabletInboundMessageEventArgs>? InboundReceived;
    public IReadOnlyList<string> ActiveSubscriptions { get; private set; } = [];
    public IReadOnlyCollection<TabletInboundMessage> RecentInbound => _inboundStore.GetRecent(MaxInboundMessages);
    public string ActiveEnvironmentName => _activeEnvironment.Name;
    public bool IsConnected => _nodeBridgeConnected || _client.IsConnected;
    public bool UsesNodeBridge => _nodeBridgeConnected;

    public async Task ConnectAndSubscribeAsync(CancellationToken cancellationToken = default)
    {
        lock (_connectLock)
        {
            _activeEnvironment = _config.PrepareEnvironmentForDevice(_config.GetActiveEnvironment());
        }

        await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
        await SubscribeInternalAsync(cancellationToken).ConfigureAwait(false);
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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(MqttConnectionProbe.DefaultTimeout);

        try
        {
            _nodeBridgeConnected = false;
            await UnsubscribeActiveAsync(timeoutCts.Token).ConfigureAwait(false);
            await StopNodeListenerAsync().ConfigureAwait(false);
            ActiveSubscriptions = [];
            if (_client.IsConnected)
            {
                log?.Info("Disconnecting current session...");
                await _client.DisconnectAsync(cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            }

            await ConnectInternalAsync(timeoutCts.Token, log).ConfigureAwait(false);
            await SubscribeInternalAsync(timeoutCts.Token, log).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Error($"Connection timed out after {MqttConnectionProbe.DefaultTimeout.TotalSeconds:0} seconds");
            throw new InvalidOperationException(
                $"Connection timed out after {MqttConnectionProbe.DefaultTimeout.TotalSeconds:0} seconds");
        }
    }

    public async Task PublishAsync(string uplinkTopic, byte[] payload, bool retain = false, CancellationToken cancellationToken = default)
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

            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(uplinkTopic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public Task PublishPresetAsync(UplinkPreset preset, CancellationToken cancellationToken = default)
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

        return PublishAsync(topic, payload, retain: false, cancellationToken);
    }

    public async Task PublishAckAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var topic = TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromAck, _config.Device.DeviceId);
        await PublishAsync(topic, TabletPayloadFactory.CreateAck(_config.Device, messageId), retain: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishRawAsync(string uplinkTopic, string filePathOrHexOrJson, bool retain = false, CancellationToken cancellationToken = default)
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
            await PublishAsync(uplinkTopic, encoded.WireBytes.ToArray(), retain, cancellationToken).ConfigureAwait(false);
            return;
        }

        var (resolvedTopic, bytes, _, topicAutoDetected, _) = MqttProtoDecoder.ResolveInput(input);
        var topic = topicAutoDetected ? resolvedTopic! : uplinkTopic;
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new InvalidOperationException("Topic is required when the input is not an MQTT PUBLISH capture.");
        }

        await PublishAsync(topic, bytes, retain, cancellationToken).ConfigureAwait(false);
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
        await UnsubscribeActiveAsync(cancellationToken).ConfigureAwait(false);
        await StopNodeListenerAsync().ConfigureAwait(false);
        _nodeBridgeConnected = false;
        ActiveSubscriptions = [];
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _client.Dispose();
    }

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
        log?.Info($"Starting Node listener as {listenerClientId} for {filters.Count} topic filter(s)...");

        var listener = await _nodeBridge.StartListenerAsync(env, listenerClientId, filters, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        listener.MessageReceived += OnNodeListenerMessage;
        listener.LogReceived += (_, line) => log?.Info(line);

        _nodeListener = listener;
        _nodeBridgeConnected = true;
        ActiveSubscriptions = filters;
        log?.Info("Connected via Node bridge — listening for downlink messages");
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
            foreach (var filter in ActiveSubscriptions)
            {
                log?.Info($"Subscribed (Node bridge): {filter}");
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
        InboundReceived?.Invoke(this, new TabletInboundMessageEventArgs(sequenced));
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
            return new InboundDecodeResult(
                MqttProtoDecoder.FormatOutput(result, decodeInner),
                fields.EventType,
                fields.EquipmentId);
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

internal sealed record InboundDecodeResult(string Summary, string? EventType, string? EquipmentId = null);

public enum UplinkPreset
{
    SyncFull,
    SyncConfig,
    Heartbeat,
    Sos,
    TaskEvent,
}
