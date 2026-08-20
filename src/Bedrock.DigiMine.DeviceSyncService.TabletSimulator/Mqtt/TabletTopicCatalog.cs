using DssMqttFilters = Bedrock.DigiMine.DeviceSyncService.Domain.Constants.MqttSubscriptionFilters;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public static class TabletTopicCatalog
{
    public static string ResolveUplinkTopic(string subscriptionFilter, string deviceId) =>
        subscriptionFilter.Replace("+", deviceId, StringComparison.Ordinal);

    public static IReadOnlyList<string> GetUplinkTopics(string deviceId) =>
    [
        ResolveUplinkTopic(DssMqttFilters.SubFromSync, deviceId),
        ResolveUplinkTopic(DssMqttFilters.SubFromEvents, deviceId),
        ResolveUplinkTopic(DssMqttFilters.SubFromSos, deviceId),
        ResolveUplinkTopic(DssMqttFilters.SubFromTelemetry, deviceId),
        ResolveUplinkTopic(DssMqttFilters.SubFromAck, deviceId),
        ResolveUplinkTopic(DssMqttFilters.SubFromFilesUrlReq, deviceId),
    ];

    public static IReadOnlyList<string> GetDownlinkSubscriptionFilters(string deviceId) =>
    [
        $"to/{deviceId}/#",
        "config/#",
    ];

    /// <summary>
    /// Uplink filters so the simulator can observe device→service traffic
    /// (including TaskCreated published by a real device or another client).
    /// </summary>
    public static IReadOnlyList<string> GetUplinkSubscriptionFilters(string deviceId) =>
    [
        $"from/{deviceId}/#",
    ];

    public static IReadOnlyList<string> GetAllSubscriptionFilters(string deviceId)
    {
        var downlink = GetDownlinkSubscriptionFilters(deviceId);
        var uplink = GetUplinkSubscriptionFilters(deviceId);
        var combined = new List<string>(downlink.Count + uplink.Count);
        combined.AddRange(downlink);
        combined.AddRange(uplink);
        return combined;
    }

    public static bool IsUplinkTopic(string topic) =>
        topic.StartsWith("from/", StringComparison.Ordinal);

    public static string ResolveUplinkKey(string deviceId, string keyOrTopic)
    {
        if (keyOrTopic.StartsWith("from/", StringComparison.Ordinal))
        {
            return keyOrTopic;
        }

        return keyOrTopic.ToUpperInvariant() switch
        {
            "SYNC" => ResolveUplinkTopic(DssMqttFilters.SubFromSync, deviceId),
            "TASKEVENTS" or "EVENTS" => ResolveUplinkTopic(DssMqttFilters.SubFromEvents, deviceId),
            "SOS" => ResolveUplinkTopic(DssMqttFilters.SubFromSos, deviceId),
            "TELEMETRY" or "HEARTBEAT" => ResolveUplinkTopic(DssMqttFilters.SubFromTelemetry, deviceId),
            "ACK" => ResolveUplinkTopic(DssMqttFilters.SubFromAck, deviceId),
            "FILES" or "FILESURL" => ResolveUplinkTopic(DssMqttFilters.SubFromFilesUrlReq, deviceId),
            _ => keyOrTopic,
        };
    }
}
