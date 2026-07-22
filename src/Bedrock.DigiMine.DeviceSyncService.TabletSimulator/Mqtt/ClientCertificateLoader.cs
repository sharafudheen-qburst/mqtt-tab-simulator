using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public static class ClientCertificateLoader
{
    internal const X509KeyStorageFlags SchannelKeyStorage =
        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;

    public static X509Certificate2? Load(MqttEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        CertificatePathHelper.ResolveFromFolder(environment.Certificates);

        var certPath = CertificatePathHelper.Normalize(environment.Certificates.ClientCertificateFile);
        var keyPath = CertificatePathHelper.Normalize(environment.Certificates.ClientKeyFile);

        if (string.IsNullOrWhiteSpace(certPath))
        {
            return null;
        }

        if (!File.Exists(certPath))
        {
            throw new FileNotFoundException($"Client certificate not found: {certPath}", certPath);
        }

        if (!string.IsNullOrWhiteSpace(keyPath) && !File.Exists(keyPath))
        {
            throw new FileNotFoundException($"Client key not found: {keyPath}", keyPath);
        }

        var extension = Path.GetExtension(certPath).ToLowerInvariant();
        if (extension is ".pfx" or ".p12")
        {
            return LoadPkcs12(certPath, environment.Password);
        }

        var certPem = File.ReadAllText(certPath);
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return LoadFromPemContent(certPem, null, certPath);
        }

        var keyPem = File.ReadAllText(keyPath!);
        return LoadFromPemContent(certPem, keyPem, $"{certPath} + {keyPath}");
    }

    public static X509Certificate2 LoadClientCertificateFromPem(string certificatePem, string privateKeyPem) =>
        LoadFromPemContent(certificatePem, privateKeyPem, "certificate and private key PEM");

    private static X509Certificate2 LoadPkcs12(string path, string? password)
    {
        try
        {
            using var loaded = new X509Certificate2(path, password, SchannelKeyStorage);
            return new X509Certificate2(loaded);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"Failed to load PKCS#12 client certificate from '{path}'. {ex.Message}", ex);
        }
    }

    private static X509Certificate2 LoadFromPemContent(string certPem, string? keyPem, string sourceDescription)
    {
        if (!string.IsNullOrWhiteSpace(keyPem) && TryLoadRsaPem(certPem, keyPem, out var rsaCertificate))
        {
            return rsaCertificate;
        }

        if (!string.IsNullOrWhiteSpace(keyPem) && TryLoadEcPem(certPem, keyPem, out var ecCertificate))
        {
            return ecCertificate;
        }

        var attempts = new List<Func<X509Certificate2>>();

        if (!string.IsNullOrWhiteSpace(keyPem))
        {
            attempts.Add(() => X509Certificate2.CreateFromPem(certPem, keyPem));
            attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"), keyPem));
            attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"), ExtractFirstPemBlock(keyPem, "PRIVATE KEY")));
            attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"), ExtractFirstPemBlock(keyPem, "RSA PRIVATE KEY")));
            attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"), ExtractFirstPemBlock(keyPem, "EC PRIVATE KEY")));
        }

        if (certPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            attempts.Add(() => X509Certificate2.CreateFromPem(certPem));
            attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"), ExtractFirstPemBlock(certPem, "PRIVATE KEY")));
            attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"), ExtractFirstPemBlock(certPem, "RSA PRIVATE KEY")));
            attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"), ExtractFirstPemBlock(certPem, "EC PRIVATE KEY")));
        }

        attempts.Add(() => X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE")));
        attempts.Add(() => X509Certificate2.CreateFromPem(certPem));

        CryptographicException? lastError = null;
        foreach (var attempt in attempts)
        {
            try
            {
                using var certificate = attempt();
                if (!certificate.HasPrivateKey && !string.IsNullOrWhiteSpace(keyPem))
                {
                    continue;
                }

                return PrepareForTlsClientAuth(certificate);
            }
            catch (CryptographicException ex)
            {
                lastError = ex;
            }
        }

        throw new CryptographicException(
            $"Failed to load client certificate from {sourceDescription}. " +
            "Ensure the client certificate and private key match, are PEM formatted, and are not password-protected. " +
            $"Last error: {lastError?.Message}",
            lastError);
    }

    private static bool TryLoadRsaPem(string certPem, string keyPem, out X509Certificate2 certificate)
    {
        certificate = null!;
        try
        {
            using var publicCert = X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);
            using var combined = publicCert.CopyWithPrivateKey(rsa);
            var pfx = combined.Export(X509ContentType.Pkcs12);
            certificate = OperatingSystem.IsWindows()
                ? new X509Certificate2(pfx, (string?)null, SchannelKeyStorage)
                : new X509Certificate2(pfx);
            return certificate.HasPrivateKey;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryLoadEcPem(string certPem, string keyPem, out X509Certificate2 certificate)
    {
        certificate = null!;
        try
        {
            using var publicCert = X509Certificate2.CreateFromPem(ExtractFirstPemBlock(certPem, "CERTIFICATE"));
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(keyPem);
            using var combined = publicCert.CopyWithPrivateKey(ecdsa);
            var pfx = combined.Export(X509ContentType.Pkcs12);
            certificate = OperatingSystem.IsWindows()
                ? new X509Certificate2(pfx, (string?)null, SchannelKeyStorage)
                : new X509Certificate2(pfx);
            return certificate.HasPrivateKey;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// PEM and ephemeral PKCS#12 loads work in OpenSSL (MQTTX) but Windows Schannel often
    /// cannot acquire client credentials unless the cert is re-imported with a persisted key.
    /// </summary>
    private static X509Certificate2 PrepareForTlsClientAuth(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            return new X509Certificate2(certificate);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new X509Certificate2(certificate);
        }

        try
        {
            var exported = certificate.Export(X509ContentType.Pkcs12);
            return new X509Certificate2(exported, (string?)null, SchannelKeyStorage);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException(
                "Client certificate loaded but could not be prepared for Windows TLS client authentication. " +
                "Try using a .pfx file instead.",
                ex);
        }
    }

    private static string ExtractFirstPemBlock(string content, string label)
    {
        var begin = $"-----BEGIN {label}-----";
        var end = $"-----END {label}-----";
        var start = content.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var finish = content.IndexOf(end, start, StringComparison.Ordinal);
        if (finish < 0)
        {
            return content;
        }

        return content[start..(finish + end.Length)];
    }
}
