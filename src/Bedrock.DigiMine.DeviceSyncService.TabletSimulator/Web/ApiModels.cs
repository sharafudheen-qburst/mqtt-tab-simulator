namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Web;

using System.Text.Json;
public sealed class PublishRequest
{
    public string Topic { get; set; } = string.Empty;

    /// <summary>Hex, file path, or JSON (JSON is encoded via ProtoDecoder MqttProtoEncoder).</summary>
    public string? Payload { get; set; }

    public string? Preset { get; set; }
    public bool Retain { get; set; }
}

public sealed class ConnectRequest
{
    public string EnvironmentName { get; set; } = string.Empty;
    public bool SaveActive { get; set; } = true;
}

public sealed class MqttAutoDisposeRequest
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes until auto disconnect. Default 60 (1 hour) when omitted or invalid.</summary>
    public int? Minutes { get; set; }
}

public sealed class CertificateUploadRequest
{
    public string EnvironmentName { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentBase64 { get; set; } = string.Empty;
}

public sealed class ExportPfxRequest
{
    public string EnvironmentName { get; set; } = string.Empty;
    public Configuration.MqttEnvironment? Environment { get; set; }
    public string? PfxPassword { get; set; }
    public bool UpdateConfig { get; set; } = true;
}

public sealed class ValidateConnectionRequest
{
    public Configuration.MqttEnvironment Environment { get; set; } = new();
    public string DeviceId { get; set; } = string.Empty;
}

public sealed class AddDeviceRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? EquipmentId { get; set; }
}

public sealed class DigiMineProxyQueryRequest
{
    public string? BearerToken { get; set; }
    public string? BaseUrl { get; set; }
    public string? OperationalUnitId { get; set; }

    /// <summary>Device or Equipment (case-insensitive). Used when Target is empty.</summary>
    public string Entity { get; set; } = "Device";

    public string? Target { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

public sealed class SelectDeviceRequest
{
    public string DeviceId { get; set; } = string.Empty;
}

public sealed class UpdateDeviceRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public class CertificateFolderRequest
{
    public string Folder { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
}

public sealed class CertificateContentRequest : CertificateFolderRequest
{
    public string FileName { get; set; } = string.Empty;
}

public sealed class DeviceCertGenerateRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "ecdsa";
}

public sealed class DeviceCertSaveBundleRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public JsonElement EnrollResponse { get; set; }
}

public sealed class LibsSyncRequest
{
    public string DssRepoRoot { get; set; } = string.Empty;
    public string Configuration { get; set; } = "Debug";
}

public sealed class DecodeRequest
{
    public string Topic { get; set; } = string.Empty;
    public string PayloadHex { get; set; } = string.Empty;
}

public sealed class EncodeRequest
{
    public string Topic { get; set; } = string.Empty;

    /// <summary>Protobuf JSON (or decoder-output text containing "Decoded payload:").</summary>
    public string Json { get; set; } = string.Empty;
}

public sealed class InboundSyncRequest
{
    public List<InboundMessageDto> Messages { get; set; } = [];
}

public sealed class InboundMessageDto
{
    public long Sequence { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Topic { get; set; } = string.Empty;
    public int PayloadLength { get; set; }
    public bool Retained { get; set; }
    public string DecodedSummary { get; set; } = string.Empty;
    public string PayloadHex { get; set; } = string.Empty;
    public string? EventType { get; set; }
    public string? EquipmentId { get; set; }
}

public sealed class AppStorageRequest
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
