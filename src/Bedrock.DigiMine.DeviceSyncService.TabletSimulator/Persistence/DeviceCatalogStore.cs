using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;

/// <summary>
/// In-memory OT catalog snapshot built from inbound CONFIG / TASK MQTT after Sync FULL.
/// </summary>
public sealed class DeviceCatalogStore
{
    private static readonly HashSet<string> DestinationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "crusher", "stockpile", "wastedump", "waste_dump", "waste dump",
    };

    private readonly object _lock = new();
    private DeviceCatalogSnapshot _snapshot = new();
    private readonly Dictionary<string, CatalogTaskCard> _tasks = new(StringComparer.OrdinalIgnoreCase);

    public DeviceCatalogSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return CloneSnapshot(_snapshot);
        }
    }

    public IReadOnlyList<CatalogTaskCard> GetTasks()
    {
        lock (_lock)
        {
            return _tasks.Values
                .OrderBy(t => t.EstimatedStartTime, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.TaskTypeName, StringComparer.OrdinalIgnoreCase)
                .Select(CloneTask)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            var ouId = _snapshot.OuId;
            var deviceId = _snapshot.DeviceId;
            var equipmentId = _snapshot.EquipmentId;
            _snapshot = new DeviceCatalogSnapshot
            {
                OuId = ouId,
                DeviceId = deviceId,
                EquipmentId = equipmentId,
                MineName = _snapshot.MineName,
                TimeZone = _snapshot.TimeZone,
            };
            _tasks.Clear();
        }
    }

    /// <summary>
    /// Bind DigiMine Settings OU. Clears OU-scoped workplaces/shifts/equipment list when OU changes.
    /// </summary>
    public void SetActiveOuId(string? ouId)
    {
        var normalized = (ouId ?? string.Empty).Trim();
        lock (_lock)
        {
            if (string.Equals(_snapshot.OuId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _snapshot.OuId = normalized;
                return;
            }

            _snapshot.OuId = normalized;
            ClearOuScopedLocked();
        }
    }

    public void ClearOuScoped()
    {
        lock (_lock)
        {
            ClearOuScopedLocked();
        }
    }

    private void ClearOuScopedLocked()
    {
        _snapshot.Workplaces = [];
        _snapshot.Materials = [];
        _snapshot.MaterialLinks = [];
        _snapshot.Shift = null;
        // Keep task types / mine / equipment / tasks — only OU-topic data is wiped.
    }

    public void EnsureDevice(string deviceId, string equipmentId)
    {
        lock (_lock)
        {
            _snapshot.DeviceId = deviceId;
            _snapshot.EquipmentId = equipmentId;
            if (_snapshot.Equipment is null && !string.IsNullOrWhiteSpace(equipmentId))
            {
                _snapshot.Equipment = new CatalogEquipment
                {
                    Id = equipmentId,
                    Name = equipmentId,
                };
            }
            else if (_snapshot.Equipment is not null && string.IsNullOrWhiteSpace(_snapshot.Equipment.Id))
            {
                _snapshot.Equipment.Id = equipmentId;
            }
        }
    }

    public void UpsertLocalTask(CatalogTaskCard task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_lock)
        {
            _tasks[task.TaskId] = CloneTask(task);
        }
    }

    public void Ingest(string topic, string? payloadJson, string? eventType)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            lock (_lock)
            {
                if (IsTopic(topic, "config/mine") || EntityEquals(root, "MineInformation") || EntityEquals(root, "Mine"))
                {
                    ApplyMine(GetInnerPayload(root));
                    return;
                }

                if (IsTopic(topic, "config/taskTypes") || EntityEquals(root, "TaskTypes"))
                {
                    ApplyTaskTypes(GetInnerPayload(root));
                    return;
                }

                if (TopicEndsWith(topic, "/workplaces") || EntityEquals(root, "Workplaces"))
                {
                    if (!IsActiveOuTopic(topic))
                    {
                        return;
                    }

                    ApplyWorkplaces(GetInnerPayload(root));
                    return;
                }

                if (TopicEndsWith(topic, "/shiftrules") || EntityEquals(root, "ShiftRules"))
                {
                    if (!IsActiveOuTopic(topic))
                    {
                        return;
                    }

                    ApplyShiftRules(GetInnerPayload(root));
                    return;
                }

                if (TopicEndsWith(topic, "/equipmentlist") || EntityEquals(root, "EquipmentList"))
                {
                    if (!IsActiveOuTopic(topic))
                    {
                        return;
                    }

                    ApplyEquipmentList(GetInnerPayload(root));
                    return;
                }

                if (Regex.IsMatch(topic, @"^to/[^/]+/config/equipment$", RegexOptions.IgnoreCase)
                    || EntityEquals(root, "Equipment"))
                {
                    ApplyDeviceEquipment(GetInnerPayload(root));
                    return;
                }

                if (topic.Contains("/taskevents", StringComparison.OrdinalIgnoreCase)
                    || LooksLikeTaskEvent(eventType, root))
                {
                    ApplyTaskEvent(root, eventType);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed decode JSON; catalog stays as last good snapshot.
        }
    }

    /// <summary>
    /// Accepts config/ou/{ouId}/… only when ouId matches DigiMine Settings OU.
    /// Non-OU topics (or missing OU in settings) are rejected for OU-scoped ingest.
    /// </summary>
    private bool IsActiveOuTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(_snapshot.OuId))
        {
            return false;
        }

        var match = Regex.Match(
            topic,
            @"^config/ou/(?<ouId>[^/]+)/(workplaces|shiftrules|equipmentlist)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            // EntityType-only match without topic OU — reject to avoid wrong-OU overwrite.
            return false;
        }

        return string.Equals(
            match.Groups["ouId"].Value,
            _snapshot.OuId,
            StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyMine(JsonElement payload)
    {
        var name = ReadString(payload, "name") ?? ReadString(payload, "mineName") ?? string.Empty;
        var tz = ReadString(payload, "timeZone") ?? ReadString(payload, "timezone") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            _snapshot.MineName = name;
        }

        if (!string.IsNullOrWhiteSpace(tz))
        {
            _snapshot.TimeZone = tz;
        }

        // Refresh mine-day label if we already have shift rules.
        if (_snapshot.Shift is not null)
        {
            _snapshot.Shift.MineDayDate = ResolveMineDayDate(
                _snapshot.Shift.AnchorTime,
                _snapshot.Shift.OperationalDay);
        }
    }

    private void ApplyTaskTypes(JsonElement payload)
    {
        if (!TryGetArray(payload, "taskTypes", out var array))
        {
            return;
        }

        var list = new List<CatalogTaskType>();
        foreach (var item in array.EnumerateArray())
        {
            var id = ReadString(item, "taskTypeId") ?? ReadString(item, "id");
            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            list.Add(new CatalogTaskType
            {
                Id = id,
                Name = name,
                WorkplaceTypes = ReadStringArray(item, "workplaceTypes"),
                PrimaryEquipmentTypes = ReadStringArray(item, "primaryEquipmentTypes"),
                DestinationAllowed = ReadString(item, "destinationAllowed") ?? string.Empty,
                MeasurementUnits = ReadString(item, "measurementUnits") ?? string.Empty,
                MultiplierUnit = ReadString(item, "multiplierUnit") ?? string.Empty,
                Multiplier = ReadDouble(item, "multiplier"),
            });
        }

        _snapshot.TaskTypes = list;
    }

    private void ApplyWorkplaces(JsonElement payload)
    {
        if (!TryGetArray(payload, "workplaces", out var array))
        {
            return;
        }

        var workplaces = new List<CatalogWorkplace>();
        var materials = new Dictionary<string, CatalogMaterial>(StringComparer.OrdinalIgnoreCase);
        var links = new List<CatalogMaterialLink>();

        foreach (var item in array.EnumerateArray())
        {
            var id = ReadString(item, "workplaceId") ?? ReadString(item, "id");
            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            workplaces.Add(new CatalogWorkplace
            {
                Id = id,
                Name = name,
                WorkplaceType = ReadString(item, "workplaceType") ?? string.Empty,
                DestinationType = ReadString(item, "destinationType") ?? string.Empty,
            });

            if (item.TryGetProperty("materials", out var mats) && mats.ValueKind == JsonValueKind.Array)
            {
                foreach (var mat in mats.EnumerateArray())
                {
                    var materialId = ReadString(mat, "id") ?? ReadString(mat, "materialId");
                    var materialName = ReadString(mat, "name");
                    if (string.IsNullOrWhiteSpace(materialId) || string.IsNullOrWhiteSpace(materialName))
                    {
                        continue;
                    }

                    materials[materialId] = new CatalogMaterial { Id = materialId, Name = materialName };
                    links.Add(new CatalogMaterialLink { MaterialId = materialId, WorkplaceId = id });
                }
            }
        }

        _snapshot.Workplaces = workplaces;
        _snapshot.Materials = materials.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _snapshot.MaterialLinks = links;
    }

    private void ApplyShiftRules(JsonElement payload)
    {
        var shiftId = ReadString(payload, "id") ?? string.Empty;
        var ruleName = ReadString(payload, "name") ?? "Shift";
        var anchorTimeUtc = ReadString(payload, "anchorTime") ?? string.Empty;
        var operationalDay = ReadString(payload, "operationalDay") ?? string.Empty;
        var mineDay = ResolveMineDayDate(anchorTimeUtc, operationalDay);
        var nowMinutes = GetMineLocalNow().Hour * 60 + GetMineLocalNow().Minute;

        if (!TryGetArray(payload, "shifts", out var shifts) || shifts.GetArrayLength() == 0)
        {
            _snapshot.Shift = new CatalogShiftInfo
            {
                ShiftId = shiftId,
                Name = ruleName,
                MineDayDate = mineDay,
                DisplayLabel = ruleName,
                AnchorTime = UtcClockToMineLocal(anchorTimeUtc) ?? anchorTimeUtc,
                OperationalDay = operationalDay,
            };
            return;
        }

        JsonElement chosen = shifts[0];
        foreach (var shift in shifts.EnumerateArray())
        {
            var startLocal = UtcClockToMineLocal(ReadString(shift, "start"));
            var endLocal = UtcClockToMineLocal(ReadString(shift, "end"));
            var start = ParseClockToMinutes(startLocal);
            var end = ParseClockToMinutes(endLocal);
            if (start is null || end is null)
            {
                continue;
            }

            if (IsWithinShiftWindow(nowMinutes, start.Value, end.Value))
            {
                chosen = shift;
                break;
            }
        }

        var startUtc = ReadString(chosen, "start") ?? string.Empty;
        var endUtc = ReadString(chosen, "end") ?? string.Empty;
        var startLocalChosen = UtcClockToMineLocal(startUtc) ?? startUtc;
        var endLocalChosen = UtcClockToMineLocal(endUtc) ?? endUtc;
        var name = ReadString(chosen, "name") ?? ruleName;
        var id = ReadString(chosen, "id") ?? shiftId;

        _snapshot.Shift = new CatalogShiftInfo
        {
            ShiftId = id,
            Name = name,
            StartTime = NormalizeHm(startLocalChosen),
            EndTime = NormalizeHm(endLocalChosen),
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            MineDayDate = mineDay,
            DisplayLabel = string.IsNullOrWhiteSpace(startLocalChosen) && string.IsNullOrWhiteSpace(endLocalChosen)
                ? name
                : $"{name} ({FormatClock(startLocalChosen)} To {FormatClock(endLocalChosen)})",
            AnchorTime = UtcClockToMineLocal(anchorTimeUtc) ?? anchorTimeUtc,
            OperationalDay = operationalDay,
        };
    }

    /// <summary>
    /// MQTT shift clocks are UTC wall times; convert to mine-local HH:mm[:ss] using current offset
    /// (same approach as tablet utcToMineLocal).
    /// </summary>
    private string? UtcClockToMineLocal(string? utcClock)
    {
        if (string.IsNullOrWhiteSpace(utcClock))
        {
            return utcClock;
        }

        var minutes = ParseClockToMinutes(utcClock);
        if (minutes is null)
        {
            return utcClock;
        }

        var hasSeconds = utcClock.Split(':').Length >= 3;
        var offsetMinutes = GetMineUtcOffsetMinutes();
        var total = minutes.Value + offsetMinutes;
        total = ((total % (24 * 60)) + (24 * 60)) % (24 * 60);
        var h = total / 60;
        var m = total % 60;
        return hasSeconds
            ? $"{h:D2}:{m:D2}:00"
            : $"{h:D2}:{m:D2}";
    }

    private int GetMineUtcOffsetMinutes()
    {
        var local = GetMineLocalNow();
        var utc = DateTime.UtcNow;
        return (int)Math.Round((local - utc).TotalMinutes);
    }

    private static string NormalizeHm(string raw)
    {
        var mins = ParseClockToMinutes(raw);
        if (mins is null)
        {
            return raw;
        }

        return $"{mins.Value / 60:D2}:{mins.Value % 60:D2}";
    }

    private DateTime GetMineLocalNow()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_snapshot.TimeZone))
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(_snapshot.TimeZone);
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
        }
        catch (TimeZoneNotFoundException)
        {
            // Fall through — IANA ids may need conversion on Windows.
        }
        catch (InvalidTimeZoneException)
        {
        }

        // Try common IANA → Windows mapping for DigiMine defaults.
        try
        {
            if (string.Equals(_snapshot.TimeZone, "Asia/Dubai", StringComparison.OrdinalIgnoreCase))
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Arabian Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }

            if (string.Equals(_snapshot.TimeZone, "Asia/Kolkata", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_snapshot.TimeZone, "Asia/Calcutta", StringComparison.OrdinalIgnoreCase))
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
        }
        catch (TimeZoneNotFoundException)
        {
        }

        return DateTime.Now;
    }

    private string ResolveMineDayDate(string? anchorTime, string? operationalDay)
    {
        var local = GetMineLocalNow();
        var label = local.Date;

        // Simplified mine-day label: use mine-local calendar date.
        // Anchor/operationalDay refine later if needed; matches tablet fallback when TZ is known.
        _ = anchorTime;
        if (string.Equals(operationalDay, "PREVIOUS_DAY", StringComparison.OrdinalIgnoreCase))
        {
            // Label is often "next calendar day" relative to overnight anchor — keep local date for Ad-Hoc.
        }

        return label.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void ApplyEquipmentList(JsonElement payload)
    {
        if (!TryGetArray(payload, "equipment", out var array)
            && !TryGetArray(payload, "items", out array)
            && !TryGetArray(payload, "equipmentList", out array))
        {
            // EquipmentListPayload uses repeated EquipmentListItem — field name is typically "equipment" or similar.
            // Fall back: scan any array of objects with equipmentId.
            array = default;
            foreach (var prop in payload.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                {
                    var first = prop.Value[0];
                    if (first.ValueKind == JsonValueKind.Object
                        && (first.TryGetProperty("equipmentId", out _) || first.TryGetProperty("name", out _)))
                    {
                        array = prop.Value;
                        break;
                    }
                }
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                return;
            }
        }

        var equipmentId = _snapshot.EquipmentId;
        foreach (var item in array.EnumerateArray())
        {
            var id = ReadString(item, "equipmentId") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(equipmentId)
                && !string.Equals(id, equipmentId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ApplyEquipmentFromItem(item, id);
            if (!string.IsNullOrWhiteSpace(equipmentId))
            {
                break;
            }
        }
    }

    private void ApplyDeviceEquipment(JsonElement payload)
    {
        var id = ReadString(payload, "equipmentId")
            ?? ReadString(payload, "id")
            ?? _snapshot.EquipmentId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        ApplyEquipmentFromItem(payload, id);
    }

    private void ApplyEquipmentFromItem(JsonElement item, string id)
    {
        var name = ReadString(item, "name") ?? id;
        var typeId = string.Empty;
        var typeName = string.Empty;
        if (item.TryGetProperty("equipmentType", out var typeEl) && typeEl.ValueKind == JsonValueKind.Object)
        {
            typeId = ReadString(typeEl, "id") ?? string.Empty;
            typeName = ReadString(typeEl, "name") ?? string.Empty;
        }
        else
        {
            typeId = ReadString(item, "equipmentTypeId") ?? ReadString(item, "typeId") ?? string.Empty;
            typeName = ReadString(item, "equipmentTypeName") ?? ReadString(item, "typeName") ?? string.Empty;
        }

        _snapshot.Equipment = new CatalogEquipment
        {
            Id = id,
            Name = name,
            TypeId = typeId,
            TypeName = typeName,
        };
        _snapshot.EquipmentId = id;
    }

    private void ApplyTaskEvent(JsonElement root, string? eventType)
    {
        var resolvedType = eventType
            ?? ReadString(root, "eventType")
            ?? string.Empty;

        if (resolvedType.Contains("Unassigned", StringComparison.OrdinalIgnoreCase)
            || resolvedType.Contains("OperatorUnassigned", StringComparison.OrdinalIgnoreCase))
        {
            var removeId = ReadString(root, "taskId");
            if (!string.IsNullOrWhiteSpace(removeId))
            {
                _tasks.Remove(removeId);
            }

            return;
        }

        if (!resolvedType.Contains("Assigned", StringComparison.OrdinalIgnoreCase)
            && !resolvedType.Contains("Created", StringComparison.OrdinalIgnoreCase)
            && !resolvedType.Contains("Updated", StringComparison.OrdinalIgnoreCase)
            && !resolvedType.Contains("Progress", StringComparison.OrdinalIgnoreCase)
            && !resolvedType.Contains("Status", StringComparison.OrdinalIgnoreCase)
            && !resolvedType.Contains("State", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var taskId = ReadString(root, "taskId");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        var payload = GetInnerPayload(root);
        var existing = _tasks.TryGetValue(taskId, out var prev) ? prev : new CatalogTaskCard { TaskId = taskId };

        var taskTypeId = ReadString(payload, "taskTypeId") ?? existing.TaskTypeId;
        var materialId = ReadString(payload, "materialId") ?? existing.MaterialId;
        var workplaceId = ReadString(root, "workplaceId") ?? existing.WorkplaceId;
        var taskTypeName = ReadString(payload, "taskType") ?? existing.TaskTypeName;
        if (string.IsNullOrWhiteSpace(taskTypeName) && !string.IsNullOrWhiteSpace(taskTypeId))
        {
            taskTypeName = _snapshot.TaskTypes.FirstOrDefault(t => t.Id == taskTypeId)?.Name ?? taskTypeName;
        }

        var workplaceName = existing.WorkplaceName;
        if (!string.IsNullOrWhiteSpace(workplaceId))
        {
            workplaceName = _snapshot.Workplaces.FirstOrDefault(w => w.Id == workplaceId)?.Name ?? workplaceName;
        }

        var materialName = existing.MaterialName;
        if (!string.IsNullOrWhiteSpace(materialId))
        {
            materialName = _snapshot.Materials.FirstOrDefault(m => m.Id == materialId)?.Name ?? materialName;
        }

        var quantity = ReadDouble(payload, "quantity");
        if (quantity <= 0)
        {
            quantity = existing.PlannedQuantity;
        }

        var startTime = ReadString(payload, "estimatedStartTime") ?? existing.EstimatedStartTime;
        var endTime = ReadString(payload, "estimatedEndTime") ?? existing.EstimatedEndTime;
        var startDate = ReadString(payload, "expectedStartDate") ?? existing.ExpectedStartDate;
        var status = ReadString(payload, "status")
            ?? ReadString(payload, "newStatus")
            ?? ReadString(payload, "newState")
            ?? existing.Status;
        var readable = ReadString(payload, "taskReadableId") ?? existing.TaskReadableId;
        var isAdHoc = ReadBool(payload, "adHoc") || existing.IsAdHoc;
        var unit = ReadString(payload, "unitOfMeasure")
            ?? _snapshot.TaskTypes.FirstOrDefault(t => t.Id == taskTypeId)?.MeasurementUnits
            ?? existing.UnitOfMeasure;

        var equipmentName = _snapshot.Equipment?.Name ?? existing.PrimaryEquipmentName;

        _tasks[taskId] = new CatalogTaskCard
        {
            TaskId = taskId,
            TaskReadableId = readable,
            TaskTypeId = taskTypeId,
            TaskTypeName = string.IsNullOrWhiteSpace(taskTypeName) ? "Task" : taskTypeName,
            WorkplaceId = workplaceId,
            WorkplaceName = workplaceName,
            MaterialId = materialId,
            MaterialName = materialName,
            PlannedQuantity = quantity,
            ActualQuantity = ReadDouble(payload, "quantityCompleted") > 0
                ? ReadDouble(payload, "quantityCompleted")
                : existing.ActualQuantity,
            UnitOfMeasure = unit,
            EstimatedStartTime = startTime,
            EstimatedEndTime = endTime,
            ExpectedStartDate = startDate,
            Status = string.IsNullOrWhiteSpace(status) ? "Assigned" : status,
            IsAdHoc = isAdHoc,
            PrimaryEquipmentName = equipmentName,
        };
    }

    private static JsonElement GetInnerPayload(JsonElement root)
    {
        if (root.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object)
        {
            return payload;
        }

        return root;
    }

    private static bool EntityEquals(JsonElement root, string entityType)
    {
        var value = ReadString(root, "entityType");
        return string.Equals(value, entityType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTaskEvent(string? eventType, JsonElement root)
    {
        if (!string.IsNullOrWhiteSpace(eventType)
            && eventType.Contains("Task", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var et = ReadString(root, "eventType");
        return !string.IsNullOrWhiteSpace(et) && et.Contains("Task", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTopic(string topic, string exact) =>
        string.Equals(topic, exact, StringComparison.OrdinalIgnoreCase);

    private static bool TopicEndsWith(string topic, string suffix) =>
        topic.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetArray(JsonElement element, string name, out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString(),
        };
    }

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetArray(element, name, out var array))
        {
            return [];
        }

        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var s = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.GetRawText(),
                _ => item.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
        }

        return list;
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) && b,
            _ => false,
        };
    }

    private static int? ParseClockToMinutes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Accept "06:00", "06:00:00", "06:00 AM"
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.Hour * 60 + dt.Minute;
        }

        var parts = raw.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[0], out var h)
            && int.TryParse(Regex.Replace(parts[1], @"\D", ""), out var m))
        {
            return h * 60 + m;
        }

        return null;
    }

    private static bool IsWithinShiftWindow(int nowMinutes, int start, int end)
    {
        if (start == end)
        {
            return true;
        }

        if (start < end)
        {
            return nowMinutes >= start && nowMinutes < end;
        }

        // Overnight shift
        return nowMinutes >= start || nowMinutes < end;
    }

    private static string FormatClock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("hh:mm tt", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static DeviceCatalogSnapshot CloneSnapshot(DeviceCatalogSnapshot source) =>
        new()
        {
            DeviceId = source.DeviceId,
            EquipmentId = source.EquipmentId,
            OuId = source.OuId,
            MineName = source.MineName,
            TimeZone = source.TimeZone,
            Equipment = source.Equipment is null
                ? null
                : new CatalogEquipment
                {
                    Id = source.Equipment.Id,
                    Name = source.Equipment.Name,
                    TypeId = source.Equipment.TypeId,
                    TypeName = source.Equipment.TypeName,
                },
            TaskTypes = source.TaskTypes.Select(t => new CatalogTaskType
            {
                Id = t.Id,
                Name = t.Name,
                WorkplaceTypes = [.. t.WorkplaceTypes],
                PrimaryEquipmentTypes = [.. t.PrimaryEquipmentTypes],
                DestinationAllowed = t.DestinationAllowed,
                MeasurementUnits = t.MeasurementUnits,
                MultiplierUnit = t.MultiplierUnit,
                Multiplier = t.Multiplier,
            }).ToList(),
            Workplaces = source.Workplaces.Select(w => new CatalogWorkplace
            {
                Id = w.Id,
                Name = w.Name,
                WorkplaceType = w.WorkplaceType,
                DestinationType = w.DestinationType,
            }).ToList(),
            Materials = source.Materials.Select(m => new CatalogMaterial { Id = m.Id, Name = m.Name }).ToList(),
            MaterialLinks = source.MaterialLinks.Select(l => new CatalogMaterialLink
            {
                MaterialId = l.MaterialId,
                WorkplaceId = l.WorkplaceId,
            }).ToList(),
            Shift = source.Shift is null
                ? null
                : new CatalogShiftInfo
                {
                    ShiftId = source.Shift.ShiftId,
                    Name = source.Shift.Name,
                    StartTime = source.Shift.StartTime,
                    EndTime = source.Shift.EndTime,
                    StartTimeUtc = source.Shift.StartTimeUtc,
                    EndTimeUtc = source.Shift.EndTimeUtc,
                    MineDayDate = source.Shift.MineDayDate,
                    DisplayLabel = source.Shift.DisplayLabel,
                    AnchorTime = source.Shift.AnchorTime,
                    OperationalDay = source.Shift.OperationalDay,
                },
        };

    private static CatalogTaskCard CloneTask(CatalogTaskCard t) =>
        new()
        {
            TaskId = t.TaskId,
            TaskReadableId = t.TaskReadableId,
            TaskTypeName = t.TaskTypeName,
            TaskTypeId = t.TaskTypeId,
            WorkplaceName = t.WorkplaceName,
            WorkplaceId = t.WorkplaceId,
            MaterialName = t.MaterialName,
            MaterialId = t.MaterialId,
            PlannedQuantity = t.PlannedQuantity,
            ActualQuantity = t.ActualQuantity,
            UnitOfMeasure = t.UnitOfMeasure,
            EstimatedStartTime = t.EstimatedStartTime,
            EstimatedEndTime = t.EstimatedEndTime,
            ExpectedStartDate = t.ExpectedStartDate,
            Status = t.Status,
            IsAdHoc = t.IsAdHoc,
            PrimaryEquipmentName = t.PrimaryEquipmentName,
        };

    public static bool IsDestinationWorkplace(CatalogWorkplace workplace) =>
        !string.IsNullOrWhiteSpace(workplace.DestinationType)
        && DestinationTypes.Contains(workplace.DestinationType.Trim());
}
