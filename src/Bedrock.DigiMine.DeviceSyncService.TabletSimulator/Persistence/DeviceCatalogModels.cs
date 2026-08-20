namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;

public sealed class DeviceCatalogSnapshot
{
    public string DeviceId { get; set; } = string.Empty;
    public string EquipmentId { get; set; } = string.Empty;
    public string OuId { get; set; } = string.Empty;
    public string MineName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public CatalogEquipment? Equipment { get; set; }
    public List<CatalogTaskType> TaskTypes { get; set; } = [];
    public List<CatalogWorkplace> Workplaces { get; set; } = [];
    public List<CatalogMaterial> Materials { get; set; } = [];
    public List<CatalogMaterialLink> MaterialLinks { get; set; } = [];
    public CatalogShiftInfo? Shift { get; set; }
    public bool HasTaskTypes => TaskTypes.Count > 0;
    public bool HasWorkplaces => Workplaces.Count > 0;
    public bool IsReady => HasTaskTypes && HasWorkplaces && !string.IsNullOrWhiteSpace(OuId);
}

public sealed class CatalogTaskType
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> WorkplaceTypes { get; set; } = [];
    public List<string> PrimaryEquipmentTypes { get; set; } = [];
    public string DestinationAllowed { get; set; } = string.Empty;
    public string MeasurementUnits { get; set; } = string.Empty;
    public string MultiplierUnit { get; set; } = string.Empty;
    public double Multiplier { get; set; }
}

public sealed class CatalogWorkplace
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WorkplaceType { get; set; } = string.Empty;
    public string DestinationType { get; set; } = string.Empty;
}

public sealed class CatalogMaterial
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class CatalogMaterialLink
{
    public string MaterialId { get; set; } = string.Empty;
    public string WorkplaceId { get; set; } = string.Empty;
}

public sealed class CatalogEquipment
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TypeId { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
}

public sealed class CatalogShiftInfo
{
    public string ShiftId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Mine-local HH:mm (or HH:mm:ss) for UI / Ad-Hoc defaults.</summary>
    public string StartTime { get; set; } = string.Empty;
    /// <summary>Mine-local HH:mm (or HH:mm:ss) for UI / Ad-Hoc defaults.</summary>
    public string EndTime { get; set; } = string.Empty;
    public string StartTimeUtc { get; set; } = string.Empty;
    public string EndTimeUtc { get; set; } = string.Empty;
    public string MineDayDate { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string AnchorTime { get; set; } = string.Empty;
    public string OperationalDay { get; set; } = string.Empty;
}

public sealed class CatalogTaskCard
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskReadableId { get; set; } = string.Empty;
    public string TaskTypeName { get; set; } = string.Empty;
    public string TaskTypeId { get; set; } = string.Empty;
    public string WorkplaceName { get; set; } = string.Empty;
    public string WorkplaceId { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string MaterialId { get; set; } = string.Empty;
    public double PlannedQuantity { get; set; }
    public double ActualQuantity { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public string EstimatedStartTime { get; set; } = string.Empty;
    public string EstimatedEndTime { get; set; } = string.Empty;
    public string ExpectedStartDate { get; set; } = string.Empty;
    public string Status { get; set; } = "Assigned";
    public bool IsAdHoc { get; set; }
    public string PrimaryEquipmentName { get; set; } = string.Empty;
}

public sealed class AdHocTaskCreateRequest
{
    public string TaskTypeId { get; set; } = string.Empty;
    public string WorkplaceId { get; set; } = string.Empty;
    public string MaterialId { get; set; } = string.Empty;
    public string? AllowedDestinationId { get; set; }
    public string Quantity { get; set; } = string.Empty;
    public string? DeadlineHours { get; set; }
    public string ExpectedStartDate { get; set; } = string.Empty;
    public string EstimatedStartTime { get; set; } = string.Empty;
    public string EstimatedEndTime { get; set; } = string.Empty;
}
