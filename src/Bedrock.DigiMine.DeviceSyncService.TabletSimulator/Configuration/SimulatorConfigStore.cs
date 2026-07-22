using System.Globalization;
using System.Text.Json;

using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;

public sealed class SimulatorConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _configPath;
    private readonly object _lock = new();

    public SimulatorConfigStore()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "simulator-config.json");
    }

    public string ConfigPath => _configPath;

    public string CertificatesRoot => Path.Combine(AppContext.BaseDirectory, "certificates");

    public SimulatorConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_configPath))
            {
                var defaults = CreateDefault();
                ApplyAppsettingsDefaults(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<SimulatorConfig>(json, JsonOptions) ?? CreateDefault();
            config.EnsureDevicesMigrated();
            ApplyAppsettingsDefaults(config);
            foreach (var env in config.Environments)
            {
                env.NormalizeHost();
                CertificatePathHelper.ResolveFromFolder(env.Certificates);
            }

            return config;
        }
    }

    public void Save(SimulatorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_lock)
        {
            foreach (var env in config.Environments)
            {
                env.NormalizeHost();
                if (!string.IsNullOrWhiteSpace(env.Certificates.Folder))
                {
                    env.Certificates.CaFile = string.Empty;
                    env.Certificates.ClientCertificateFile = string.Empty;
                    env.Certificates.ClientKeyFile = string.Empty;
                }
            }

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
    }

    public string SaveUploadedCertificate(string environmentName, string fieldName, string originalFileName, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(content);

        var safeName = string.Join('_', environmentName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dir = Path.Combine(CertificatesRoot, safeName);
        Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = fieldName switch
            {
                "caFile" => ".crt",
                "clientCertificateFile" => ".crt",
                "clientKeyFile" => ".key",
                _ => ".pem",
            };
        }

        var fileName = $"{fieldName}{ext}";
        var fullPath = Path.Combine(dir, fileName);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    public static SimulatorConfig LoadFromArgs(string[] args, SimulatorConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(store);

        var config = store.Load();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToUpperInvariant())
            {
                case "--DEVICE-ID" when i + 1 < args.Length:
                    config.Device.DeviceId = args[++i];
                    break;
                case "--EQUIPMENT-ID" when i + 1 < args.Length:
                    config.Device.EquipmentId = args[++i];
                    break;
                case "--ENV" when i + 1 < args.Length:
                    config.ActiveEnvironment = args[++i];
                    break;
                case "--WEB-PORT" when i + 1 < args.Length:
                    config.Web.Port = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--CONSOLE":
                    config.Web.UseConsole = true;
                    break;
                case "--SKIP-LIB-SYNC":
                    config.Libs ??= new LibsOptions();
                    config.Libs.SyncOnStartup = false;
                    break;
            }
        }

        return config;
    }

    private static void ApplyAppsettingsDefaults(SimulatorConfig config)
    {
        var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(appsettingsPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
            if (!document.RootElement.TryGetProperty("deviceCert", out var deviceCert))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(config.DeviceCert.OutputFolder)
                && deviceCert.TryGetProperty("outputFolder", out var outputFolder)
                && outputFolder.ValueKind == JsonValueKind.String)
            {
                var value = outputFolder.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    config.DeviceCert.OutputFolder = value;
                }
            }

            if (string.IsNullOrWhiteSpace(config.DeviceCert.DssEnrollBaseUrl)
                && deviceCert.TryGetProperty("dssEnrollBaseUrl", out var enrollBaseUrl)
                && enrollBaseUrl.ValueKind == JsonValueKind.String)
            {
                var value = enrollBaseUrl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    config.DeviceCert.DssEnrollBaseUrl = value;
                }
            }
        }
        catch (JsonException)
        {
            // Ignore invalid appsettings.json and keep simulator-config values.
        }
    }

    private static SimulatorConfig CreateDefault()
    {
        var deviceId = Guid.NewGuid().ToString();
        var equipmentId = Guid.NewGuid().ToString();
        return new SimulatorConfig
        {
            ActiveEnvironment = "LOCAL",
            Device = new DeviceOptions
            {
                DeviceId = deviceId,
                EquipmentId = equipmentId,
            },
            Devices =
            [
                new DeviceEntry
                {
                    DeviceId = deviceId,
                    EquipmentId = equipmentId,
                },
            ],
            Environments =
            [
                new MqttEnvironment
                {
                    Name = "LOCAL",
                    Host = "localhost",
                    Port = 1883,
                    Username = "dss",
                    KeepAliveSeconds = 60,
                },
            ],
        };
    }
}
