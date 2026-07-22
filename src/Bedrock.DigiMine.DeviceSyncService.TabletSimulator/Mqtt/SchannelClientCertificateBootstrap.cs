using System.Security.Cryptography.X509Certificates;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

/// <summary>
/// Windows Schannel performs mTLS outside the process and often refuses to present
/// PEM-loaded client certificates unless the issuing CA is trusted locally and the
/// client certificate lives in the CurrentUser\My store.
/// </summary>
public static class SchannelClientCertificateBootstrap
{
    public static X509Certificate2 PrepareForHandshake(
        X509Certificate2 clientCertificate,
        byte[]? caCertificateBytes,
        ConnectionAttemptLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(clientCertificate);

        if (!OperatingSystem.IsWindows())
        {
            return clientCertificate;
        }

        X509Certificate2? caCertificate = null;
        try
        {
            if (caCertificateBytes is { Length: > 0 })
            {
                caCertificate = new X509Certificate2(caCertificateBytes);
                EnsureCertificateInStore(StoreName.Root, caCertificate, log, "CA certificate");
            }

            if (!clientCertificate.HasPrivateKey)
            {
                log?.Error("Client certificate has no private key after load");
                return clientCertificate;
            }

            ValidateLocalChain(clientCertificate, caCertificate, log);
            return EnsureClientCertificateInPersonalStore(clientCertificate, log);
        }
        finally
        {
            caCertificate?.Dispose();
        }
    }

    private static void ValidateLocalChain(
        X509Certificate2 clientCertificate,
        X509Certificate2? caCertificate,
        ConnectionAttemptLog? log)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

        if (caCertificate is not null)
        {
            chain.ChainPolicy.ExtraStore.Add(caCertificate);
            chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        }

        if (chain.Build(clientCertificate))
        {
            log?.Info("Client certificate chain validated locally for Schannel");
            return;
        }

        var errors = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
        log?.Error($"Client certificate chain validation failed locally: {errors}");
    }

    private static X509Certificate2 EnsureClientCertificateInPersonalStore(
        X509Certificate2 clientCertificate,
        ConnectionAttemptLog? log)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            clientCertificate.Thumbprint,
            validOnly: false);

        for (var i = 0; i < existing.Count; i++)
        {
            var candidate = existing[i];
            if (candidate.HasPrivateKey)
            {
                log?.Info("Using client certificate from CurrentUser\\My store");
                return new X509Certificate2(candidate);
            }
        }

        var pfx = clientCertificate.Export(X509ContentType.Pkcs12);
        var imported = new X509Certificate2(
            pfx,
            (string?)null,
            ClientCertificateLoader.SchannelKeyStorage);

        store.Add(imported);
        log?.Info("Installed client certificate into CurrentUser\\My store for Schannel mTLS");

        return new X509Certificate2(imported);
    }

    private static void EnsureCertificateInStore(
        StoreName storeName,
        X509Certificate2 certificate,
        ConnectionAttemptLog? log,
        string label)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certificate.Thumbprint,
            validOnly: false);

        if (existing.Count > 0)
        {
            log?.Info($"{label} already present in CurrentUser\\{storeName}");
            return;
        }

        store.Add(new X509Certificate2(certificate));
        log?.Info($"Installed {label} into CurrentUser\\{storeName} for Schannel");
    }
}
