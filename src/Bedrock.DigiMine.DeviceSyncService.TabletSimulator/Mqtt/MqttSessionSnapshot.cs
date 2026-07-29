namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public sealed record MqttSessionSnapshot(
    string DeviceId,
    string? Name,
    string EquipmentId,
    string Environment,
    string Broker,
    bool Connected,
    bool UsesNodeBridge,
    DateTimeOffset? ConnectedAt,
    bool AutoDisposeEnabled,
    int AutoDisposeMinutes,
    DateTimeOffset? AutoDisposeAt,
    IReadOnlyList<string> Subscriptions,
    bool SubscriptionsReady = false,
    IReadOnlyList<string>? ConfirmedSubscriptions = null);
