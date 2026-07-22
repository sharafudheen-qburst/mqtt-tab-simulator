using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public static class CertificatePathHelper
{
    public const string CaFileName = "ca.crt";
    public const string ClientCertFileName = "client.crt";
    public const string ClientKeyFileName = "client.key";

    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// When <see cref="CertificatePaths.Folder"/> is set, resolves fixed file names inside that folder.
    /// </summary>
    public static void ResolveFromFolder(CertificatePaths certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        var folder = Normalize(certificates.Folder);
        if (folder is null)
        {
            TryInferFolderFromLegacyPaths(certificates);
            folder = Normalize(certificates.Folder);
        }

        if (folder is null)
        {
            certificates.CaFile = Normalize(certificates.CaFile) ?? string.Empty;
            certificates.ClientCertificateFile = Normalize(certificates.ClientCertificateFile) ?? string.Empty;
            certificates.ClientKeyFile = Normalize(certificates.ClientKeyFile) ?? string.Empty;
            return;
        }

        certificates.Folder = folder;
        certificates.CaFile = Path.Combine(folder, CaFileName);
        certificates.ClientCertificateFile = Path.Combine(folder, ClientCertFileName);
        certificates.ClientKeyFile = Path.Combine(folder, ClientKeyFileName);
    }

    private static void TryInferFolderFromLegacyPaths(CertificatePaths certificates)
    {
        var ca = Normalize(certificates.CaFile);
        if (ca is null)
        {
            return;
        }

        var dir = Path.GetDirectoryName(ca);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        var expectedCa = Path.Combine(dir, CaFileName);
        var expectedCert = Path.Combine(dir, ClientCertFileName);
        var expectedKey = Path.Combine(dir, ClientKeyFileName);
        if (File.Exists(expectedCa) && File.Exists(expectedCert) && File.Exists(expectedKey))
        {
            certificates.Folder = dir;
        }
    }

    public static string DescribeFile(string label, string? path, ConnectionAttemptLog log)
    {
        var normalized = Normalize(path);
        if (normalized is null)
        {
            log.Info($"{label}: (not set)");
            return "not set";
        }

        if (File.Exists(normalized))
        {
            log.Info($"{label}: found at {normalized}");
            return "found";
        }

        log.Error($"{label}: file not found at {normalized}");
        return "missing";
    }

    public static void DescribeCertificateFolder(CertificatePaths certificates, ConnectionAttemptLog log)
    {
        ResolveFromFolder(certificates);

        var folder = Normalize(certificates.Folder);
        if (folder is not null)
        {
            log.Info($"Certificate folder: {folder}");
            log.Info($"  Expected: {CaFileName}, {ClientCertFileName}, {ClientKeyFileName}");
        }

        DescribeFile("CA file", certificates.CaFile, log);
        DescribeFile("Client certificate", certificates.ClientCertificateFile, log);
        DescribeFile("Client key", certificates.ClientKeyFile, log);
    }

    public static IReadOnlyList<CertificateFileDescriptor> ListFolderFiles(string? folder)
    {
        var normalizedFolder = Normalize(folder);
        if (normalizedFolder is null)
        {
            return [];
        }

        var descriptors = new[]
        {
            new CertificateFileDescriptor(CaFileName, "CA certificate"),
            new CertificateFileDescriptor(ClientCertFileName, "Client certificate"),
            new CertificateFileDescriptor(ClientKeyFileName, "Client private key"),
        };

        var results = new List<CertificateFileDescriptor>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            var fullPath = Path.GetFullPath(Path.Combine(normalizedFolder, descriptor.FileName));
            var folderFullPath = Path.GetFullPath(normalizedFolder);
            if (!fullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exists = File.Exists(fullPath);
            results.Add(descriptor with
            {
                Path = fullPath,
                Exists = exists,
                SizeBytes = exists ? new FileInfo(fullPath).Length : 0,
            });
        }

        return results;
    }

    public static string ReadFolderFile(string? folder, string fileName)
    {
        var normalizedFolder = Normalize(folder)
            ?? throw new InvalidOperationException("Certificate folder is not set.");

        if (!IsAllowedFileName(fileName))
        {
            throw new InvalidOperationException($"Unsupported certificate file: {fileName}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(normalizedFolder, fileName));
        var folderFullPath = Path.GetFullPath(normalizedFolder);
        if (!fullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Certificate file path is outside the configured folder.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Certificate file not found: {fullPath}", fullPath);
        }

        return File.ReadAllText(fullPath);
    }

    private static bool IsAllowedFileName(string fileName) =>
        string.Equals(fileName, CaFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, ClientCertFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, ClientKeyFileName, StringComparison.OrdinalIgnoreCase);
}

public sealed record CertificateFileDescriptor(
    string FileName,
    string Label,
    string Path = "",
    bool Exists = false,
    long SizeBytes = 0);
