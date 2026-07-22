using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public static class ClientPkcs12Exporter
{
    public sealed class ExportResult
    {
        public string PfxPath { get; init; } = string.Empty;
        public bool UsedPassword { get; init; }
        public IReadOnlyList<string> Log { get; init; } = [];
    }

    public static ExportResult ExportFromEnvironment(
        MqttEnvironment environment,
        SimulatorConfigStore store,
        string? pfxPassword = null,
        ConnectionAttemptLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(store);

        log ??= new ConnectionAttemptLog();
        environment.NormalizeHost();
        CertificatePathHelper.ResolveFromFolder(environment.Certificates);

        var certPath = CertificatePathHelper.Normalize(environment.Certificates.ClientCertificateFile);
        if (string.IsNullOrWhiteSpace(certPath))
        {
            throw new InvalidOperationException("Client certificate path is required.");
        }

        var exportPassword = pfxPassword ?? environment.Password;
        X509Certificate2? withKey = null;
        try
        {
            var extension = Path.GetExtension(certPath);
            if (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
            {
                log.Info("Client certificate is already PKCS#12; re-exporting for Windows Schannel.");
                if (!string.IsNullOrEmpty(exportPassword))
                {
                    environment.Password = exportPassword;
                }

                withKey = ClientCertificateLoader.Load(environment)
                    ?? throw new InvalidOperationException("Failed to load existing PFX.");
            }
            else
            {
                var keyPath = CertificatePathHelper.Normalize(environment.Certificates.ClientKeyFile);
                if (string.IsNullOrWhiteSpace(keyPath))
                {
                    throw new InvalidOperationException("Client key path is required to build a PFX from PEM files.");
                }

                log.Info("Loading PEM client certificate + key...");
                withKey = ClientCertificateLoader.Load(environment)
                    ?? throw new InvalidOperationException("Failed to load client certificate.");

                if (!withKey.HasPrivateKey)
                {
                    throw new CryptographicException("Client certificate has no private key.");
                }
            }

            log.Info($"Exporting PKCS#12 (subject: {withKey.Subject})...");
            var pfxBytes = string.IsNullOrEmpty(exportPassword)
                ? withKey.Export(X509ContentType.Pkcs12)
                : withKey.Export(X509ContentType.Pkcs12, exportPassword);

            var pfxPath = WritePfxFile(store, environment.Name, pfxBytes);
            log.Info($"PFX written to {pfxPath}");

            return new ExportResult
            {
                PfxPath = pfxPath,
                UsedPassword = !string.IsNullOrEmpty(exportPassword),
                Log = log.Entries,
            };
        }
        finally
        {
            withKey?.Dispose();
        }
    }

    private static string WritePfxFile(SimulatorConfigStore store, string environmentName, byte[] pfxBytes)
    {
        var safeName = string.Join('_', environmentName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dir = Path.Combine(store.CertificatesRoot, safeName);
        Directory.CreateDirectory(dir);
        var pfxPath = Path.Combine(dir, "client.pfx");
        File.WriteAllBytes(pfxPath, pfxBytes);
        return pfxPath;
    }
}
