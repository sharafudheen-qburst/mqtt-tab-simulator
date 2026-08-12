using Bedrock.DigiMine.DeviceSyncService.ProtoDecoder;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using Bedrock.DigiMine.Protos.OT;
using Google.Protobuf;
using DssMqttFilters = Bedrock.DigiMine.DeviceSyncService.Domain.Constants.MqttSubscriptionFilters;

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

    /// <summary>
    /// Builds Sync FULL uplink topic, wire-faithful JSON (device/equipment IDs filled), and hex payload.
    /// </summary>
    public static (string Topic, string Json, string PayloadHex, string MessageType) CreateSyncFullPreview(
        DeviceOptions device)
    {
        var topic = TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromSync, device.DeviceId);
        var bytes = CreateSyncRequest(device);
        var decoded = MqttProtoDecoder.Decode(topic, bytes, "sync-full preset");
        var json = MqttProtoDecoder.FormatPayloadJson(decoded.Root, decoded.WireBytes, decodeInnerPayload: false);
        return (topic, json, Convert.ToHexString(bytes), decoded.MessageType);
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

    public static byte[] CreateTaskEvent(DeviceOptions device, EventType eventType = EventType.TaskCreated)
    {
        if (!TryParseTaskEventType(eventType.ToString(), out var resolved))
        {
            resolved = EventType.TaskCreated;
        }

        var envelope = CreateTaskEnvelope(device, resolved);
        envelope.Payload = BuildTaskInnerPayload(device, resolved).ToByteString();
        return envelope.ToByteArray();
    }

    /// <summary>
    /// Builds events uplink topic, wire-faithful JSON with nested inner payload, and hex.
    /// </summary>
    public static (string Topic, string Json, string PayloadHex, string MessageType, string EventType) CreateTaskEventPreview(
        DeviceOptions device,
        string? eventTypeName = null)
    {
        if (!TryParseTaskEventType(eventTypeName, out var eventType))
        {
            eventType = EventType.TaskCreated;
        }

        var topic = TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromEvents, device.DeviceId);
        var bytes = CreateTaskEvent(device, eventType);
        var decoded = MqttProtoDecoder.Decode(topic, bytes, "task-event preset");
        var json = MqttProtoDecoder.FormatPayloadJson(decoded.Root, decoded.WireBytes, decodeInnerPayload: true);
        return (topic, json, Convert.ToHexString(bytes), decoded.MessageType, eventType.ToString());
    }

    public static bool TryParseTaskEventType(string? eventTypeName, out EventType eventType)
    {
        eventType = EventType.TaskCreated;
        if (string.IsNullOrWhiteSpace(eventTypeName))
        {
            return false;
        }

        if (!Enum.TryParse(eventTypeName.Trim(), ignoreCase: true, out EventType parsed))
        {
            return false;
        }

        if (!IsSupportedTaskEventType(parsed))
        {
            return false;
        }

        eventType = parsed;
        return true;
    }

    public static bool IsSupportedTaskEventType(EventType eventType) =>
        eventType is EventType.TaskCreated
            or EventType.TaskAssigned
            or EventType.TaskProgressUpdated
            or EventType.TaskStateChanged
            or EventType.TaskStatusChanged
            or EventType.TaskUnassigned
            or EventType.TaskOperatorUnassigned
            or EventType.NoteCreated;

    private static EventEnvelope CreateTaskEnvelope(DeviceOptions device, EventType eventType)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var taskId = Guid.NewGuid().ToString();
        var shiftId = Guid.NewGuid().ToString();
        var operatorId = Guid.NewGuid().ToString();
        var workplaceId = Guid.NewGuid().ToString();

        var envelope = CreateEnvelope(device, eventType);
        envelope.Timestamp = now;
        envelope.EventTime = now;
        envelope.ShiftId = shiftId;
        envelope.OperatorId = operatorId;

        if (eventType != EventType.NoteCreated)
        {
            envelope.TaskId = taskId;
        }

        if (eventType is EventType.TaskCreated
            or EventType.TaskAssigned
            or EventType.TaskUnassigned
            or EventType.TaskStatusChanged)
        {
            envelope.WorkplaceId = workplaceId;
        }

        return envelope;
    }

    private static IMessage BuildTaskInnerPayload(DeviceOptions device, EventType eventType)
    {
        var now = DateTimeOffset.UtcNow;
        var today = now.ToString("yyyy-MM-dd");
        var startTime = now.ToString("HH:mm:ss");
        var endTime = now.AddHours(1).ToString("HH:mm:ss");
        var nowMs = now.ToUnixTimeMilliseconds();

        return eventType switch
        {
            EventType.TaskCreated => new TaskCreatedPayload
            {
                TaskType = "Hauling",
                TaskTypeId = Guid.NewGuid().ToString(),
                MaterialId = Guid.NewGuid().ToString(),
                Quantity = 10,
                PlannedEquipmentId = device.EquipmentId,
                ExpectedStartDate = today,
                EstimatedStartTime = startTime,
                EstimatedEndTime = endTime,
                AdHoc = true,
                DeadlineHours = 2,
                IsHaulingTask = true,
            },
            EventType.TaskAssigned => new TaskAssignedPayload
            {
                TaskType = "Hauling",
                TaskTypeId = Guid.NewGuid().ToString(),
                MaterialId = Guid.NewGuid().ToString(),
                Quantity = 10,
                PlannedEquipmentId = device.EquipmentId,
                ExpectedStart = nowMs,
                ExpectedEnd = nowMs + (long)TimeSpan.FromHours(1).TotalMilliseconds,
                IsHaulingTask = true,
                OperatorId = Guid.NewGuid().ToString(),
                AssignedOperatorId = Guid.NewGuid().ToString(),
                TaskAssignedAt = nowMs,
                Status = "Assigned",
                DeadlineHours = 2,
                PrimaryEquipmentId = device.EquipmentId,
                TaskReadableId = "TTX000000001",
            },
            EventType.TaskProgressUpdated => new TaskProgressUpdatedPayload
            {
                QuantityCompleted = 1,
                UnitOfMeasure = "Tonnes",
                Multiplier = 1,
                UnitCount = 1,
            },
            EventType.TaskStateChanged => new TaskStateChangedPayload
            {
                OldState = "Assigned",
                NewState = "Started",
                ActualTime = nowMs,
            },
            EventType.TaskStatusChanged => new TaskStatusUpdatedPayload
            {
                OldStatus = "Planned",
                NewStatus = "Active",
                ActualTime = nowMs,
            },
            EventType.TaskUnassigned => new TaskUnassignedPayload
            {
                UnassignedBy = Guid.NewGuid().ToString(),
            },
            EventType.TaskOperatorUnassigned => new TaskOperatorUnassignedPayload
            {
                UnassignedBy = Guid.NewGuid().ToString(),
            },
            EventType.NoteCreated => new NoteCreatedPayload
            {
                EntityId = Guid.NewGuid().ToString(),
                EntityType = "Task",
                Note = "Tablet simulator note",
                AuthorId = Guid.NewGuid().ToString(),
                AuthorName = "tablet-simulator",
            },
            _ => throw new InvalidOperationException($"Unsupported task event type: {eventType}"),
        };
    }

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
