using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public static class MqttConnectionProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public sealed class ProbeResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? Step { get; init; }
        public long ElapsedMs { get; init; }
        public string Broker { get; init; } = string.Empty;
        public IReadOnlyList<string> Log { get; init; } = [];
    }

    public static async Task<ProbeResult> ValidateAsync(
        MqttEnvironment environment,
        string deviceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var log = new ConnectionAttemptLog();
        var sw = Stopwatch.StartNew();
        var env = CloneEnvironment(environment);
        env.NormalizeHost();
        var broker = env.GetBrokerUrl();
        var effectiveTimeout = timeout ?? DefaultTimeout;

        log.Info($"Validating MQTT connection to {broker} (timeout {effectiveTimeout.TotalSeconds:0}s)");

        if (NodeMqttBridgeService.ShouldUseNodeBridge(env))
        {
            return await ValidateViaNodeBridgeAsync(env, deviceId, broker, sw, log, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            ValidateCertificates(env, log);
            var clientId = string.IsNullOrWhiteSpace(env.ClientId) ? deviceId : env.ClientId;
            log.Info($"Client ID: {clientId}");
            log.Info($"Transport: {(env.SslTls ? "mqtts (TLS over TCP)" : "mqtt (plain TCP)")}, strict validation: {env.SslSecure}");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            var factory = new MqttFactory();
            using var client = factory.CreateMqttClient();
            var options = BuildOptions(env, clientId, log);

            if (env.SslTls)
            {
                log.Info($"Opening MQTT connection to {broker} (TLS 1.2, SNI: {env.GetTlsTargetHost()})...");
            }
            else
            {
                log.Info($"Opening MQTT connection to {broker}...");
            }
            var connectResult = await client.ConnectAsync(options, timeoutCts.Token).ConfigureAwait(false);
            if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
            {
                log.Error($"MQTT CONNACK: {connectResult.ResultCode}");
                return Fail(sw, broker, log, "mqtt", $"MQTT connect failed: {connectResult.ResultCode}");
            }

            log.Info($"MQTT CONNACK: {connectResult.ResultCode}");
            if (client.IsConnected)
            {
                log.Info("Disconnecting probe client");
                await client.DisconnectAsync(cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            }

            log.Info("Validation succeeded");
            return new ProbeResult
            {
                Ok = true,
                Broker = broker,
                ElapsedMs = sw.ElapsedMilliseconds,
                Log = log.Entries,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            log.Error($"Timed out after {effectiveTimeout.TotalSeconds:0} seconds");
            return Fail(sw, broker, log, "timeout", $"Connection timed out after {effectiveTimeout.TotalSeconds:0} seconds");
        }
        catch (Exception ex)
        {
            return MapException(sw, broker, log, ex);
        }
    }

    public static MqttClientOptions BuildOptions(MqttEnvironment env, string clientId, ConnectionAttemptLog? log = null)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(env.Host, env.Port)
            .WithCleanSession(env.CleanSession)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(env.KeepAliveSeconds));

        if (!string.IsNullOrWhiteSpace(env.Username))
        {
            builder.WithCredentials(env.Username, env.Password ?? string.Empty);
        }

        MqttTlsConfigurator.Apply(builder, env, log);
        return builder.Build();
    }

    public static void ValidateCertificates(MqttEnvironment env, ConnectionAttemptLog log)
    {
        CertificatePathHelper.ResolveFromFolder(env.Certificates);

        if (!env.SslTls)
        {
            log.Info("TLS disabled — certificate files not required");
            return;
        }

        log.Info("Checking certificate files on disk...");
        CertificatePathHelper.DescribeCertificateFolder(env.Certificates, log);

        if (!string.IsNullOrWhiteSpace(env.Certificates.ClientCertificateFile))
        {
            log.Info("Loading client certificate...");
            var loaded = ClientCertificateLoader.Load(env);
            if (loaded is null)
            {
                log.Info("No client certificate configured (broker may not require mutual TLS)");
                return;
            }

            try
            {
                byte[]? caBytes = null;
                if (!string.IsNullOrWhiteSpace(env.Certificates.CaFile) && File.Exists(env.Certificates.CaFile))
                {
                    caBytes = File.ReadAllBytes(env.Certificates.CaFile);
                }

                using var prepared = SchannelClientCertificateBootstrap.PrepareForHandshake(loaded, caBytes, log);
                log.Info(prepared.HasPrivateKey
                    ? $"Client certificate ready for TLS (subject: {prepared.Subject})"
                    : "Client certificate loaded (no private key)");
            }
            finally
            {
                loaded.Dispose();
            }
        }
        else
        {
            log.Info("No client certificate configured (broker may not require mutual TLS)");
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

    private static ProbeResult Fail(Stopwatch sw, string broker, ConnectionAttemptLog log, string step, string error) =>
        new()
        {
            Ok = false,
            Broker = broker,
            Step = step,
            Error = error,
            ElapsedMs = sw.ElapsedMilliseconds,
            Log = log.Entries,
        };

    private static ProbeResult MapException(Stopwatch sw, string broker, ConnectionAttemptLog log, Exception ex)
    {
        log.Error(ex, "Connection failed");

        var (step, message) = ex switch
        {
            FileNotFoundException fnf => ("cert", fnf.Message),
            CryptographicException ce => ("cert", ce.Message),
            InvalidOperationException ioe => ("config", ioe.Message),
            MqttCommunicationException mce => ("mqtt", ExceptionDetailFormatter.Format(mce)),
            System.Security.Authentication.AuthenticationException auth => ("tls", ExceptionDetailFormatter.Format(auth)),
            SocketException se => ("tcp", $"{se.SocketErrorCode}: {se.Message}"),
            _ => ("unknown", ExceptionDetailFormatter.Format(ex)),
        };

        return Fail(sw, broker, log, step, message);
    }

    private static async Task<ProbeResult> ValidateViaNodeBridgeAsync(
        MqttEnvironment env,
        string deviceId,
        string broker,
        Stopwatch sw,
        ConnectionAttemptLog log,
        TimeSpan effectiveTimeout,
        CancellationToken cancellationToken)
    {
        var clientId = string.IsNullOrWhiteSpace(env.ClientId) ? deviceId : env.ClientId;
        log.Info("Using Node.js OpenSSL MQTT bridge (bypasses Windows Schannel)");
        log.Info($"Client ID: {clientId}");
        log.Info($"Transport: mqtts (Node/OpenSSL), strict validation: {env.SslSecure}");

        env.Certificates.CaFile = CertificatePathHelper.Normalize(env.Certificates.CaFile) ?? string.Empty;
        env.Certificates.ClientCertificateFile =
            CertificatePathHelper.Normalize(env.Certificates.ClientCertificateFile) ?? string.Empty;
        env.Certificates.ClientKeyFile = CertificatePathHelper.Normalize(env.Certificates.ClientKeyFile) ?? string.Empty;

        CertificatePathHelper.ResolveFromFolder(env.Certificates);

        log.Info("Checking certificate files for Node bridge...");
        CertificatePathHelper.DescribeCertificateFolder(env.Certificates, log);

        try
        {
            var bridge = new NodeMqttBridgeService();
            var result = await bridge.ValidateAsync(env, clientId, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            foreach (var line in SplitLines(result.StandardOutput))
            {
                log.Info(line);
            }

            foreach (var line in SplitLines(result.StandardError))
            {
                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    log.Error(line);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    log.Info(line);
                }
            }

            if (result.Ok)
            {
                log.Info("Validation succeeded (Node OpenSSL bridge)");
                return new ProbeResult
                {
                    Ok = true,
                    Broker = broker,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Log = log.Entries,
                };
            }

            log.Error(result.Error ?? "Node bridge validation failed");
            return Fail(sw, broker, log, "node-bridge", result.Error ?? "Node bridge validation failed");
        }
        catch (Exception ex)
        {
            return MapException(sw, broker, log, ex);
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
