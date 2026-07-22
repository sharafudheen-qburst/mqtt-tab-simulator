using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.DeviceCert;

public static partial class DeviceCertService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new();

    public static DeviceCertGenerateResult Generate(string deviceId, string algorithm)
    {
        var normalizedId = deviceId.Trim();
        if (!IsValidDeviceId(normalizedId))
        {
            throw new InvalidOperationException("Device ID must be a UUID.");
        }

        var keyAlgorithm = NormalizeAlgorithm(algorithm);
        var (csrPem, privateKeyPem) = keyAlgorithm == "rsa"
            ? GenerateRsa(normalizedId)
            : GenerateEcdsa(normalizedId);

        csrPem = NormalizePem(csrPem);
        privateKeyPem = NormalizePem(privateKeyPem);

        return new DeviceCertGenerateResult
        {
            DeviceId = normalizedId,
            Algorithm = keyAlgorithm,
            KeyAlgorithm = keyAlgorithm == "rsa" ? "RSA 2048" : "ECDSA secp521r1",
            CsrPem = csrPem,
            PrivateKeyPem = privateKeyPem,
            EnrollPayloadJson = BuildEnrollPayloadJson(csrPem),
        };
    }

    public static string BuildEnrollUrl(string baseUrl, string deviceId)
    {
        var trimmedBase = baseUrl.Trim().TrimEnd('/');
        return $"{trimmedBase}/devices/{deviceId.Trim()}/enroll";
    }

    /// <summary>
    /// Builds Postman registration URL. Enroll uses /api/v1; registration uses /api/v1.0.
    /// Example: {{DssBaseUrl}}/api/v1.0/devices/{deviceId}/registration
    /// </summary>
    public static string BuildRegistrationUrl(string enrollBaseUrl, string deviceId)
    {
        var trimmedBase = enrollBaseUrl.Trim().TrimEnd('/');
        if (trimmedBase.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmedBase = string.Concat(trimmedBase.AsSpan(0, trimmedBase.Length - "/api/v1".Length), "/api/v1.0");
        }
        else if (!trimmedBase.Contains("/api/v1.0", StringComparison.OrdinalIgnoreCase))
        {
            trimmedBase = $"{trimmedBase}/api/v1.0";
        }

        return $"{trimmedBase}/devices/{deviceId.Trim()}/registration";
    }

    public static string BuildEnrollPayloadJson(string csrPem) =>
        JsonSerializer.Serialize(new { csrPem = NormalizePem(csrPem) }, CompactJsonOptions);

    public static string BuildRegistrationPayloadJson(string equipmentId) =>
        JsonSerializer.Serialize(new { equipmentId = equipmentId.Trim() }, CompactJsonOptions);

    public static bool IsValidDeviceId(string deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) && DeviceIdRegex().IsMatch(deviceId.Trim());

    public static string NormalizePem(string pem) =>
        pem.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizeAlgorithm(string algorithm) =>
        string.Equals(algorithm.Trim(), "rsa", StringComparison.OrdinalIgnoreCase) ? "rsa" : "ecdsa";

    private static (string CsrPem, string PrivateKeyPem) GenerateRsa(string deviceId)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={deviceId}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return (
            PemEncoding.WriteString("CERTIFICATE REQUEST", request.CreateSigningRequest()),
            PemEncoding.WriteString("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));
    }

    private static (string CsrPem, string PrivateKeyPem) GenerateEcdsa(string deviceId)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        var request = new CertificateRequest($"CN={deviceId}", ecdsa, HashAlgorithmName.SHA256);
        return (
            PemEncoding.WriteString("CERTIFICATE REQUEST", request.CreateSigningRequest()),
            PemEncoding.WriteString("PRIVATE KEY", ecdsa.ExportPkcs8PrivateKey()));
    }

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex DeviceIdRegex();
}

public sealed class DeviceCertGenerateResult
{
    public string DeviceId { get; init; } = string.Empty;
    public string Algorithm { get; init; } = string.Empty;
    public string KeyAlgorithm { get; init; } = string.Empty;
    public string CsrPem { get; init; } = string.Empty;
    public string PrivateKeyPem { get; init; } = string.Empty;
    public string EnrollPayloadJson { get; init; } = string.Empty;
}
