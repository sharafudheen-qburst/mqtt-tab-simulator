using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using MQTTnet;
using MQTTnet.Client;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public static class MqttTlsConfigurator
{
    public static void Apply(MqttClientOptionsBuilder builder, MqttEnvironment environment, ConnectionAttemptLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.SslTls)
        {
            return;
        }

        environment.NormalizeHost();
        CertificatePathHelper.ResolveFromFolder(environment.Certificates);

        environment.Certificates.CaFile = CertificatePathHelper.Normalize(environment.Certificates.CaFile) ?? string.Empty;
        environment.Certificates.ClientCertificateFile = CertificatePathHelper.Normalize(environment.Certificates.ClientCertificateFile) ?? string.Empty;
        environment.Certificates.ClientKeyFile = CertificatePathHelper.Normalize(environment.Certificates.ClientKeyFile) ?? string.Empty;

        var caBytes = TryReadFile(environment.Certificates.CaFile);
        X509Certificate2? clientCert = null;
        try
        {
            var loaded = ClientCertificateLoader.Load(environment);
            if (loaded is not null)
            {
                clientCert = SchannelClientCertificateBootstrap.PrepareForHandshake(loaded, caBytes, log);
                if (!ReferenceEquals(loaded, clientCert))
                {
                    loaded.Dispose();
                }
            }

            var targetHost = environment.GetTlsTargetHost();
            log?.Info($"TLS transport: mqtts:// (UseTls, target host: {targetHost})");

            builder.WithTlsOptions(tls =>
            {
                tls.UseTls(true);
                tls.WithSslProtocols(SslProtocols.Tls12);
                tls.WithTargetHost(targetHost);

                if (!string.IsNullOrWhiteSpace(environment.Alpn))
                {
                    tls.WithApplicationProtocols([new SslApplicationProtocol(environment.Alpn)]);
                }

                if (clientCert is not null)
                {
                    tls.WithClientCertificates([clientCert]);
                    log?.Info($"TLS client certificate configured: {clientCert.Subject} (thumbprint {clientCert.Thumbprint})");
                }
                else
                {
                    log?.Info("TLS client certificate not configured");
                }

                if (environment.SslSecure)
                {
                    log?.Info("Server TLS validation: strict (SSL secure on)");
                    tls.WithCertificateValidationHandler(context =>
                        ValidateServerCertificate(context, caBytes, log, acceptOnFailure: false));
                    return;
                }

                log?.Info("Server TLS validation: permissive (SSL secure off, MQTTX-style)");
                tls.WithCertificateValidationHandler(context =>
                    ValidateServerCertificate(context, caBytes, log, acceptOnFailure: true));
            });
        }
        catch
        {
            clientCert?.Dispose();
            throw;
        }
    }

    private static bool ValidateServerCertificate(
        MqttClientCertificateValidationEventArgs context,
        byte[]? caBytes,
        ConnectionAttemptLog? log,
        bool acceptOnFailure)
    {
        using var server = new X509Certificate2(context.Certificate);
        log?.Info($"Broker server certificate: subject={server.Subject}, issuer={server.Issuer}, thumbprint={server.Thumbprint}");

        if (context.SslPolicyErrors != SslPolicyErrors.None)
        {
            log?.Info($"Broker SSL policy flags: {context.SslPolicyErrors}");
        }

        if (caBytes is null)
        {
            log?.Info(acceptOnFailure
                ? "No CA file configured; accepting broker certificate"
                : "No CA file configured; rejecting broker certificate");
            return acceptOnFailure;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.Add(new X509Certificate2(caBytes));

        var ok = chain.Build(server);

        if (ok)
        {
            log?.Info("Broker certificate chains to configured CA");
            return true;
        }

        foreach (var status in chain.ChainStatus)
        {
            log?.Error($"Broker chain: {status.Status} — {status.StatusInformation.Trim()}");
        }

        if (acceptOnFailure)
        {
            log?.Info("Accepting broker certificate anyway (SSL secure off)");
            return true;
        }

        log?.Error("Rejecting broker certificate (SSL secure on)");
        return false;
    }

    private static byte[]? TryReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllBytes(path);
    }
}
