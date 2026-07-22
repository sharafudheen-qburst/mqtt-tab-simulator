using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using Bedrock.DigiMine.Protos.OT;
using Google.Protobuf;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public static class TabletPayloadFactory
{
    public static byte[] CreateSyncRequest(DeviceOptions device, string syncType = "FULL", string? shiftId = null)
    {
        var request = new SyncRequest
        {
            Envelope = CreateEnvelope(device, EventType.Sync),
            Payload = new SyncPayload { Type = syncType, ShiftId = shiftId ?? string.Empty },
        };
        return request.ToByteArray();
    }

    public static byte[] CreateHeartbeat()
    {
        var payload = new DeviceHeartbeatPayload
        {
            BatteryLevel = 85,
            NetworkType = "wifi",
            AppVersion = "tablet-simulator",
        };
        return payload.ToByteArray();
    }

    public static byte[] CreateSos(DeviceOptions device)
    {
        var sos = new SosEvent
        {
            DeviceId = device.DeviceId,
            EquipmentId = device.EquipmentId,
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        return sos.ToByteArray();
    }

    public static byte[] CreateAck(DeviceOptions device, string messageId) =>
        new AckMessage
        {
            Version = "1",
            MessageId = messageId,
            EquipmentId = device.EquipmentId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Status = AckStatus.Success,
        }.ToByteArray();

    public static byte[] CreateTaskEvent(DeviceOptions device, EventType eventType = EventType.TaskCreated) =>
        CreateEnvelope(device, eventType).ToByteArray();

    private static EventEnvelope CreateEnvelope(DeviceOptions device, EventType eventType) =>
        new()
        {
            MessageId = Guid.NewGuid().ToString(),
            DeviceId = device.DeviceId,
            EquipmentId = device.EquipmentId,
            EventType = eventType,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Version = "1",
            Priority = 1,
        };
}
