namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;

public sealed class SimulatorConfig
{
    public string ActiveEnvironment { get; set; } = "LOCAL";
    public DeviceOptions Device { get; set; } = new();
    public List<DeviceEntry> Devices { get; set; } = [];
    public WebOptions Web { get; set; } = new();
    public DeviceCertOptions DeviceCert { get; set; } = new();
    public LibsOptions Libs { get; set; } = new();
    public DigiMineOptions DigiMine { get; set; } = new();
    public List<MqttEnvironment> Environments { get; set; } = [];

    public void EnsureDevicesMigrated()
    {
        if (Devices.Count > 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Device.DeviceId))
        {
            Devices.Add(new DeviceEntry
            {
                DeviceId = Device.DeviceId.Trim(),
                EquipmentId = string.IsNullOrWhiteSpace(Device.EquipmentId)
                    ? Guid.NewGuid().ToString()
                    : Device.EquipmentId.Trim(),
            });
        }
    }

    public DeviceEntry? FindDevice(string deviceId) =>
        Devices.Find(d => string.Equals(d.DeviceId, deviceId.Trim(), StringComparison.OrdinalIgnoreCase));

    public void SelectDevice(string deviceId)
    {
        var entry = FindDevice(deviceId)
            ?? throw new InvalidOperationException($"Device '{deviceId}' not found.");

        Device.DeviceId = entry.DeviceId;
        Device.EquipmentId = entry.EquipmentId;
    }

    public DeviceEntry AddDevice(string deviceId, string? name = null, string? equipmentId = null)
    {
        var normalized = deviceId.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Device ID is required.");
        }

        if (FindDevice(normalized) is not null)
        {
            throw new InvalidOperationException($"Device '{normalized}' already exists.");
        }

        var entry = new DeviceEntry
        {
            DeviceId = normalized,
            EquipmentId = string.IsNullOrWhiteSpace(equipmentId)
                ? Guid.NewGuid().ToString()
                : equipmentId.Trim(),
            Name = name?.Trim() ?? string.Empty,
        };
        Devices.Add(entry);
        return entry;
    }

    public void SyncActiveDeviceEntry()
    {
        var entry = FindDevice(Device.DeviceId);
        if (entry is not null)
        {
            entry.EquipmentId = Device.EquipmentId;
        }
    }

    public void SetDeviceCertificateFolder(string deviceId, string? certificateFolder)
    {
        var entry = FindDevice(deviceId)
            ?? throw new InvalidOperationException($"Device '{deviceId}' not found.");
        entry.CertificateFolder = certificateFolder?.Trim() ?? string.Empty;
    }

    public string ResolveCertificateFolderForDevice(string? deviceId = null)
    {
        var id = string.IsNullOrWhiteSpace(deviceId) ? Device.DeviceId : deviceId.Trim();
        var entry = FindDevice(id);
        if (!string.IsNullOrWhiteSpace(entry?.CertificateFolder))
        {
            return entry.CertificateFolder.Trim();
        }

        // Fallback: environment default (legacy configs before per-device certs).
        try
        {
            return GetActiveEnvironment().Certificates.Folder?.Trim() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns a clone of <paramref name="environment"/> with certificates resolved for the given device
    /// (device folder wins; otherwise environment folder).
    /// </summary>
    public MqttEnvironment PrepareEnvironmentForDevice(MqttEnvironment environment, string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var clone = CloneEnvironment(environment);
        clone.NormalizeHost();
        var folder = ResolveCertificateFolderForDevice(deviceId);
        clone.Certificates.Folder = folder;
        clone.Certificates.CaFile = string.Empty;
        clone.Certificates.ClientCertificateFile = string.Empty;
        clone.Certificates.ClientKeyFile = string.Empty;
        return clone;
    }

    public void MigrateEnvironmentCertificatesToDevices()
    {
        foreach (var env in Environments)
        {
            var folder = env.Certificates.Folder?.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var leaf = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(leaf))
            {
                continue;
            }

            var match = FindDevice(leaf);
            if (match is not null && string.IsNullOrWhiteSpace(match.CertificateFolder))
            {
                match.CertificateFolder = folder;
            }
        }

        if (Devices.Count == 1 && string.IsNullOrWhiteSpace(Devices[0].CertificateFolder))
        {
            foreach (var env in Environments)
            {
                if (!string.IsNullOrWhiteSpace(env.Certificates.Folder))
                {
                    Devices[0].CertificateFolder = env.Certificates.Folder.Trim();
                    break;
                }
            }
        }
    }

    private static MqttEnvironment CloneEnvironment(MqttEnvironment source) =>
        new()
        {
            Name = source.Name,
            Host = source.Host,
            Port = source.Port,
            ClientId = source.ClientId,
            Username = source.Username,
            Password = source.Password,
            SslTls = source.SslTls,
            SslSecure = source.SslSecure,
            Alpn = source.Alpn,
            CertificateType = source.CertificateType,
            CleanSession = source.CleanSession,
            KeepAliveSeconds = source.KeepAliveSeconds,
            UseNodeMqttBridge = source.UseNodeMqttBridge,
            Certificates = new CertificatePaths
            {
                Folder = source.Certificates.Folder,
                CaFile = source.Certificates.CaFile,
                ClientCertificateFile = source.Certificates.ClientCertificateFile,
                ClientKeyFile = source.Certificates.ClientKeyFile,
            },
        };

    public DeviceEntry UpdateDeviceName(string deviceId, string? name)
    {
        var entry = FindDevice(deviceId)
            ?? throw new InvalidOperationException($"Device '{deviceId}' not found.");
        entry.Name = name?.Trim() ?? string.Empty;
        return entry;
    }

    public MqttEnvironment GetActiveEnvironment()
    {
        var env = Environments.Find(e => string.Equals(e.Name, ActiveEnvironment, StringComparison.OrdinalIgnoreCase));
        if (env is null)
        {
            throw new InvalidOperationException($"Environment '{ActiveEnvironment}' not found.");
        }

        return env;
    }
}

public sealed class DeviceOptions
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string EquipmentId { get; set; } = Guid.NewGuid().ToString();
}

public sealed class DeviceEntry
{
    public string DeviceId { get; set; } = string.Empty;
    public string EquipmentId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Optional label to identify this device/equipment pair (e.g. tablet name, location).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Per-device MQTT cert folder (ca.crt, client.crt, client.key). Used when this device is active.</summary>
    public string CertificateFolder { get; set; } = string.Empty;
}

public sealed class WebOptions
{
    public int Port { get; set; } = 5055;
    public bool UseConsole { get; set; }
}

public sealed class DeviceCertOptions
{
    public string DssEnrollBaseUrl { get; set; } = "https://localhost:5004/api/v1";

    /// <summary>Root folder for device cert bundles. Files are saved under {OutputFolder}/{deviceId}/.</summary>
    public string OutputFolder { get; set; } = string.Empty;
}

public sealed class LibsOptions
{
    /// <summary>Path to bedrock.digimine.devicesyncservice checkout (Domain + ProtoDecoder source).</summary>
    public string DssRepoRoot { get; set; } = string.Empty;

    /// <summary>When true, run scripts/sync-libs.ps1 on simulator startup (best-effort).</summary>
    public bool SyncOnStartup { get; set; } = true;
}

public sealed class DigiMineOptions
{
    public string ConfigurationBaseUrl { get; set; } = "https://digimineconfigurationdev1.irh.ae";

    public string OperationalUnitId { get; set; } = "dcf5b0e5-5489-4020-81b3-6377e1d66034";

    /// <summary>Common/query target for device inventory.</summary>
    public string DeviceQueryTarget { get; set; } =
        "/configurations?type=object&source=device&categoryId=bgt.mining.devices";

    /// <summary>Common/query target for equipment inventory.</summary>
    public string EquipmentQueryTarget { get; set; } =
        "/configurations?type=object&source=equipment&categoryId=bgt.mining.equipments";
}

public sealed class MqttEnvironment
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool SslTls { get; set; }
    public bool SslSecure { get; set; }
    public string Alpn { get; set; } = string.Empty;
    public string CertificateType { get; set; } = "CA";
    public bool CleanSession { get; set; }
    public int KeepAliveSeconds { get; set; } = 60;
    public bool UseNodeMqttBridge { get; set; }
    public CertificatePaths Certificates { get; set; } = new();

    public void NormalizeHost()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return;
        }

        if (!Host.Contains("://", StringComparison.Ordinal))
        {
            return;
        }

        if (!Uri.TryCreate(Host, UriKind.Absolute, out var uri))
        {
            return;
        }

        Host = uri.Host;
        if (uri.Port > 0)
        {
            Port = uri.Port;
        }

        if (string.Equals(uri.Scheme, "mqtts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "ssl", StringComparison.OrdinalIgnoreCase))
        {
            SslTls = true;
        }
        else if (string.Equals(uri.Scheme, "mqtt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            SslTls = false;
        }
    }

    /// <summary>MQTTX-style broker URL, e.g. mqtts://10.10.127.155:31884.</summary>
    public string GetBrokerUrl()
    {
        var scheme = SslTls ? "mqtts" : "mqtt";
        return $"{scheme}://{Host}:{Port}";
    }

    /// <summary>SNI / TLS target host (hostname from broker URL).</summary>
    public string GetTlsTargetHost() => Host;
}

public sealed class CertificatePaths
{
    /// <summary>Folder containing ca.crt, client.crt, and client.key (MQTTX-style layout).</summary>
    public string Folder { get; set; } = string.Empty;

    public string CaFile { get; set; } = string.Empty;
    public string ClientCertificateFile { get; set; } = string.Empty;
    public string ClientKeyFile { get; set; } = string.Empty;
}
