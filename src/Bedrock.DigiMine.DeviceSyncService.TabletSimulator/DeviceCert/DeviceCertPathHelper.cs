namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.DeviceCert;

public static class DeviceCertPathHelper
{
    public static string DefaultOutputRoot =>
        Path.Combine(AppContext.BaseDirectory, "device-certs");

    public static string ResolveBaseFolder(string? configuredOutputFolder, string? environmentCertFolder)
    {
        if (!string.IsNullOrWhiteSpace(configuredOutputFolder))
        {
            return configuredOutputFolder.Trim();
        }

        var fromEnvironment = TryGetBaseFromEnvironmentCertFolder(environmentCertFolder);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return DefaultOutputRoot;
    }

    public static string ResolveDeviceFolder(string baseRoot, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var normalizedId = deviceId.Trim();
        Directory.CreateDirectory(baseRoot);

        var first = Path.Combine(baseRoot, normalizedId);
        if (!Directory.Exists(first))
        {
            return first;
        }

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(baseRoot, $"{normalizedId}-{i}");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    public static string? TryGetBaseFromEnvironmentCertFolder(string? environmentCertFolder)
    {
        if (string.IsNullOrWhiteSpace(environmentCertFolder))
        {
            return null;
        }

        var folder = environmentCertFolder.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(folder);
        if (DeviceCertService.IsValidDeviceId(name))
        {
            var parent = Path.GetDirectoryName(folder);
            return string.IsNullOrWhiteSpace(parent) ? null : parent;
        }

        if (Directory.Exists(folder)
            && (File.Exists(Path.Combine(folder, "ca.crt"))
                || File.Exists(Path.Combine(folder, "client.crt"))
                || File.Exists(Path.Combine(folder, "client.key"))))
        {
            return null;
        }

        return folder;
    }
}
