namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public sealed record TabletInboundMessage(
    long Sequence,
    DateTimeOffset ReceivedAt,
    string Topic,
    int PayloadLength,
    bool Retained,
    string DecodedSummary,
    string PayloadHex,
    string? EventType = null,
    string? EquipmentId = null);
