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
    /// Builds a SyncRequest uplink (FULL / CONFIG / STATE / TASK / TUM_LIST / TUM_STATE) with wire-faithful JSON and hex.
    /// TASK, TUM_LIST, and TUM_STATE include a generated <c>shiftId</c> for DSS queries.
    /// </summary>
    public static (string Topic, string Json, string PayloadHex, string MessageType, string SyncType) CreateSyncPreview(
        DeviceOptions device,
        string? syncType = "FULL")
    {
        var type = NormalizeSyncType(syncType);
        var shiftId = NeedsShiftId(type) ? Guid.NewGuid().ToString() : null;
        var topic = TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromSync, device.DeviceId);
        var bytes = CreateSyncRequest(device, type, shiftId);
        var decoded = MqttProtoDecoder.Decode(topic, bytes, $"sync-{type.ToLowerInvariant()} preset");
        var json = MqttProtoDecoder.FormatPayloadJson(decoded.Root, decoded.WireBytes, decodeInnerPayload: false);
        return (topic, json, Convert.ToHexString(bytes), decoded.MessageType, type);
    }

    public static string NormalizeSyncType(string? syncType)
    {
        var type = (syncType ?? "FULL").Trim().ToUpperInvariant();
        return type switch
        {
            "FULL" or "CONFIG" or "STATE" or "TASK" or "TUM_LIST" or "TUM_STATE" => type,
            _ => throw new ArgumentException(
                "Sync type must be FULL, CONFIG, STATE, TASK, TUM_LIST, or TUM_STATE.",
                nameof(syncType)),
        };
    }

    public static bool NeedsShiftId(string syncType) =>
        syncType is "TASK" or "TUM_LIST" or "TUM_STATE";

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
    /// Builds a TASK_CREATED uplink from Ad-Hoc form selections (catalog IDs, not random GUIDs).
    /// </summary>
    public static (string Topic, byte[] Bytes, string TaskId, string Json) CreateAdHocTaskCreated(
        DeviceOptions device,
        Persistence.AdHocTaskCreateRequest request,
        Persistence.DeviceCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);

        var taskType = catalog.TaskTypes.FirstOrDefault(t =>
            string.Equals(t.Id, request.TaskTypeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Selected task type was not found in the synced catalog.");
        var workplace = catalog.Workplaces.FirstOrDefault(w =>
            string.Equals(w.Id, request.WorkplaceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Selected workplace was not found in the synced catalog.");
        var material = catalog.Materials.FirstOrDefault(m =>
            string.Equals(m.Id, request.MaterialId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Selected material was not found in the synced catalog.");

        var destinationRequired = IsDestinationAllowed(taskType.DestinationAllowed);
        if (destinationRequired)
        {
            if (string.IsNullOrWhiteSpace(request.AllowedDestinationId))
            {
                throw new InvalidOperationException("Allowed Destination is required for this task type.");
            }

            var destination = catalog.Workplaces.FirstOrDefault(w =>
                string.Equals(w.Id, request.AllowedDestinationId, StringComparison.OrdinalIgnoreCase)
                && Persistence.DeviceCatalogStore.IsDestinationWorkplace(w));
            if (destination is null)
            {
                throw new InvalidOperationException("Selected Allowed Destination was not found in the synced catalog.");
            }
        }

        if (!double.TryParse(request.Quantity, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity)
            || quantity <= 0)
        {
            throw new InvalidOperationException("Quantity must be a positive number.");
        }

        double? deadlineHours = null;
        if (!string.IsNullOrWhiteSpace(request.DeadlineHours))
        {
            if (!double.TryParse(request.DeadlineHours, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var deadline)
                || deadline <= 0
                || deadline >= 24)
            {
                throw new InvalidOperationException("Deadline must be between 0 and 24 hours.");
            }

            deadlineHours = deadline;
        }

        var startTime = NormalizeTimeForWire(request.EstimatedStartTime);
        var endTime = NormalizeTimeForWire(request.EstimatedEndTime);
        var startDate = string.IsNullOrWhiteSpace(request.ExpectedStartDate)
            ? (catalog.Shift?.MineDayDate ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"))
            : request.ExpectedStartDate.Trim();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var taskId = Guid.NewGuid().ToString();
        var shiftId = catalog.Shift?.ShiftId;
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            shiftId = Guid.NewGuid().ToString();
        }

        var equipmentId = catalog.Equipment?.Id ?? device.EquipmentId;
        var isHauling = taskType.Name.Contains("haul", StringComparison.OrdinalIgnoreCase);

        var inner = new TaskCreatedPayload
        {
            TaskType = taskType.Name,
            TaskTypeId = taskType.Id,
            MaterialId = material.Id,
            Quantity = quantity,
            PlannedEquipmentId = equipmentId,
            ExpectedStartDate = startDate,
            EstimatedStartTime = startTime,
            EstimatedEndTime = endTime,
            AdHoc = true,
            IsHaulingTask = isHauling,
        };
        if (deadlineHours is not null)
        {
            inner.DeadlineHours = deadlineHours.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.AllowedDestinationId))
        {
            inner.AllowedDestinationId = request.AllowedDestinationId.Trim();
        }

        var envelope = new EventEnvelope
        {
            MessageId = Guid.NewGuid().ToString(),
            DeviceId = device.DeviceId,
            EquipmentId = equipmentId,
            EventType = EventType.TaskCreated,
            Timestamp = now,
            EventTime = now,
            Version = "1",
            Priority = 1,
            TaskId = taskId,
            ShiftId = shiftId,
            WorkplaceId = workplace.Id,
            Payload = inner.ToByteString(),
        };

        var topic = TabletTopicCatalog.ResolveUplinkTopic(DssMqttFilters.SubFromEvents, device.DeviceId);
        var bytes = envelope.ToByteArray();
        var decoded = MqttProtoDecoder.Decode(topic, bytes, "adhoc-task-created");
        var json = MqttProtoDecoder.FormatPayloadJson(decoded.Root, decoded.WireBytes, decodeInnerPayload: true);
        return (topic, bytes, taskId, json);
    }

    private static string NormalizeTimeForWire(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Estimated start and end times are required.");
        }

        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        return raw.Trim();
    }

    private static bool IsDestinationAllowed(string? destinationAllowed)
    {
        if (string.IsNullOrWhiteSpace(destinationAllowed))
        {
            return false;
        }

        var v = destinationAllowed.Trim();
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
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
            or EventType.NoteCreated
            or EventType.WorkplaceChecklistSubmitted;

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
            or EventType.TaskStatusChanged
            or EventType.WorkplaceChecklistSubmitted)
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
            EventType.WorkplaceChecklistSubmitted => BuildWorkplaceChecklistPayload(nowMs),
            _ => throw new InvalidOperationException($"Unsupported task event type: {eventType}"),
        };
    }

    private static WorkplaceChecklistSubmittedPayload BuildWorkplaceChecklistPayload(long submittedTimeMs)
    {
        var submission = new ChecklistSubmission
        {
            ChecklistId = Guid.NewGuid().ToString(),
            Type = "workplace",
            SubmittedBy = Guid.NewGuid().ToString(),
            Notes = "Tablet simulator workplace checklist",
            SubmittedTime = submittedTimeMs,
            ShiftId = Guid.NewGuid().ToString(),
        };
        submission.Items.Add(new ChecklistItemResult
        {
            Id = "wp-item-01",
            Name = "Area clear of personnel",
            Status = "Good",
        });
        submission.Items.Add(new ChecklistItemResult
        {
            Id = "wp-item-02",
            Name = "Ground conditions acceptable",
            Status = "Good",
        });

        return new WorkplaceChecklistSubmittedPayload
        {
            Checklists = { submission },
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
