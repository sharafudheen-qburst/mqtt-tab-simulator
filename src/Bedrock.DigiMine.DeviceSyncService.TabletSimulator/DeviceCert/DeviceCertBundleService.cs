using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.DeviceCert;

public static class DeviceCertBundleService
{
    private static readonly TimeSpan PfxTimeout = TimeSpan.FromSeconds(5);

    public static DeviceCertBundleResult SaveBundle(
        string outputFolder,
        string deviceId,
        string privateKeyPem,
        JsonElement enrollResponse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        var certificatePem = DeviceCertService.NormalizePem(FindPem(enrollResponse,
            "certificatePem", "certPem", "issuedCertPem", "clientCertificatePem", "certificate"));
        var caPem = DeviceCertService.NormalizePem(FindPem(enrollResponse, "caChainPem", "caPem"));
        privateKeyPem = DeviceCertService.NormalizePem(privateKeyPem);

        if (string.IsNullOrWhiteSpace(certificatePem) || string.IsNullOrWhiteSpace(caPem))
        {
            throw new InvalidOperationException("Enroll response must include certificatePem and caChainPem.");
        }

        Directory.CreateDirectory(outputFolder);

        var caPath = Path.Combine(outputFolder, "ca.crt");
        var clientPath = Path.Combine(outputFolder, "client.crt");
        var keyPath = Path.Combine(outputFolder, "client.key");

        File.WriteAllText(caPath, caPem + "\n");
        File.WriteAllText(clientPath, certificatePem + "\n");
        File.WriteAllText(keyPath, privateKeyPem + "\n");

        var files = new List<string> { caPath, clientPath, keyPath };
        var certKeyWarning = TryGetCertificateKeyMismatchWarning(certificatePem, privateKeyPem);

        string? pfxWarning = null;
        var pfxPath = Path.Combine(outputFolder, "client.pfx");
        var pfxResult = TryCreatePfxWithTimeout(certificatePem, privateKeyPem, caPem, pfxPath);
        if (pfxResult.Success)
        {
            files.Add(pfxPath);
        }
        else if (pfxResult.Error is not null)
        {
            pfxWarning = pfxResult.Error;
        }

        return new DeviceCertBundleResult
        {
            DeviceId = deviceId.Trim(),
            OutputDir = outputFolder,
            Files = files,
            CertKeyWarning = certKeyWarning,
            PfxWarning = pfxWarning,
        };
    }

    private static string? TryGetCertificateKeyMismatchWarning(string certificatePem, string privateKeyPem)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromPem(ExtractPemBlock(certificatePem, "CERTIFICATE"));

            if (TryPublicKeyMatchesRsa(cert, privateKeyPem, out var rsaMatches))
            {
                return rsaMatches ? null : BuildMismatchMessage(certificatePem, privateKeyPem);
            }

            if (TryPublicKeyMatchesEc(cert, privateKeyPem, out var ecMatches))
            {
                return ecMatches ? null : BuildMismatchMessage(certificatePem, privateKeyPem);
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Could not verify certificate matches private key: {ex.Message}";
        }
    }

    private static string BuildMismatchMessage(string certificatePem, string privateKeyPem)
    {
        var certAlgorithm = DescribeCertificateAlgorithm(certificatePem);
        var keyAlgorithm = DescribePrivateKeyAlgorithm(privateKeyPem);
        return
            "The enrolled certificate may not match the generated private key. " +
            $"Certificate appears to be {certAlgorithm}; private key appears to be {keyAlgorithm}. " +
            "ca.crt, client.crt, and client.key were saved anyway. " +
            "MQTT TLS may fail until you enroll with the matching CSR/key pair.";
    }

    private static bool TryPublicKeyMatchesRsa(X509Certificate2 cert, string privateKeyPem, out bool matches)
    {
        matches = false;
        using var certPublicKey = cert.GetRSAPublicKey();
        if (certPublicKey is null)
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var certParams = certPublicKey.ExportParameters(false);
            var keyParams = rsa.ExportParameters(false);
            matches = certParams.Modulus!.AsSpan().SequenceEqual(keyParams.Modulus!)
                && certParams.Exponent!.AsSpan().SequenceEqual(keyParams.Exponent!);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryPublicKeyMatchesEc(X509Certificate2 cert, string privateKeyPem, out bool matches)
    {
        matches = false;
        using var certPublicKey = cert.GetECDsaPublicKey();
        if (certPublicKey is null)
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            var certParams = certPublicKey.ExportParameters(false);
            var keyParams = ecdsa.ExportParameters(false);
            matches = certParams.Q.X!.AsSpan().SequenceEqual(keyParams.Q.X!)
                && certParams.Q.Y!.AsSpan().SequenceEqual(keyParams.Q.Y!);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string DescribeCertificateAlgorithm(string certificatePem)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromPem(ExtractPemBlock(certificatePem, "CERTIFICATE"));
            return cert.PublicKey.Oid?.Value switch
            {
                "1.2.840.10045.2.1" => "ECDSA",
                "1.2.840.113549.1.1.1" => "RSA",
                _ => cert.PublicKey.Oid?.FriendlyName ?? "unknown",
            };
        }
        catch
        {
            return "unknown";
        }
    }

    private static string DescribePrivateKeyAlgorithm(string privateKeyPem)
    {
        if (privateKeyPem.Contains("BEGIN EC PRIVATE KEY", StringComparison.Ordinal)
            || privateKeyPem.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal)
                && privateKeyPem.Contains("EC PARAMETERS", StringComparison.Ordinal))
        {
            return "ECDSA";
        }

        if (privateKeyPem.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal))
        {
            return "RSA";
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            return "ECDSA";
        }
        catch (CryptographicException)
        {
            // not EC
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            return "RSA";
        }
        catch (CryptographicException)
        {
            return "unknown";
        }
    }

    private static string ExtractPemBlock(string content, string label)
    {
        var begin = $"-----BEGIN {label}-----";
        var end = $"-----END {label}-----";
        var start = content.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var finish = content.IndexOf(end, start, StringComparison.Ordinal);
        return finish < 0 ? content : content[start..(finish + end.Length)];
    }

    private static string FindPem(JsonElement payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }
        }

        return string.Empty;
    }

    private static (bool Success, string? Error) TryCreatePfxWithTimeout(
        string certificatePem,
        string privateKeyPem,
        string caPem,
        string pfxPath)
    {
        var task = Task.Run(() => TryCreatePfx(certificatePem, privateKeyPem, caPem, pfxPath));
        if (!task.Wait(PfxTimeout))
        {
            return (false, $"client.pfx not created: timed out after {PfxTimeout.TotalSeconds:0}s");
        }

        return task.Result;
    }

    private static (bool Success, string? Error) TryCreatePfx(
        string certificatePem,
        string privateKeyPem,
        string caPem,
        string pfxPath)
    {
        try
        {
            var certBlock = ExtractPemBlock(certificatePem, "CERTIFICATE");
            var keyBlock = ExtractPrivateKeyBlock(privateKeyPem);
            using var clientCert = X509Certificate2.CreateFromPem(certBlock, keyBlock);
            using var caCert = X509Certificate2.CreateFromPem(ExtractPemBlock(caPem, "CERTIFICATE"));
            using var clientCopy = new X509Certificate2(clientCert);
            using var caCopy = new X509Certificate2(caCert);
            var collection = new X509Certificate2Collection { clientCopy, caCopy };
            var pfxBytes = collection.Export(X509ContentType.Pkcs12)
                ?? throw new CryptographicException("PKCS#12 export returned no data.");
            File.WriteAllBytes(pfxPath, pfxBytes);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"client.pfx not created: {ex.Message}");
        }
    }

    private static string ExtractPrivateKeyBlock(string privateKeyPem)
    {
        foreach (var label in new[] { "PRIVATE KEY", "RSA PRIVATE KEY", "EC PRIVATE KEY" })
        {
            var block = ExtractPemBlock(privateKeyPem, label);
            if (block.Contains($"BEGIN {label}", StringComparison.Ordinal))
            {
                return block;
            }
        }

        return privateKeyPem;
    }
}

public sealed class DeviceCertBundleResult
{
    public string DeviceId { get; init; } = string.Empty;
    public string OutputDir { get; init; } = string.Empty;
    public IReadOnlyList<string> Files { get; init; } = [];
    public string? CertKeyWarning { get; init; }
    public string? PfxWarning { get; init; }
}
