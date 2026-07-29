using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

/// <summary>
/// Spawns the Node.js OpenSSL MQTT bridge to bypass Windows Schannel mTLS limitations.
/// </summary>
public sealed class NodeMqttBridgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    private readonly string _scriptPath;
    private readonly string _nodeExecutable;
    private readonly string _workingDirectory;

    public NodeMqttBridgeService(
        string? scriptPath = null,
        string? nodeExecutable = null,
        string? workingDirectory = null)
    {
        _scriptPath = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "NodeBridge", "mqtt-bridge.js");
        _nodeExecutable = nodeExecutable
            ?? Environment.GetEnvironmentVariable("NODE_BINARY")
            ?? "node";
        _workingDirectory = workingDirectory ?? ResolveWorkingDirectory(_scriptPath);
    }

    private static string ResolveWorkingDirectory(string scriptPath)
    {
        var scriptDir = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(scriptDir, "node_modules")))
        {
            return scriptDir;
        }

        var devDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "NodeBridge"));
        return Directory.Exists(Path.Combine(devDir, "node_modules")) ? devDir : scriptDir;
    }

    public async Task<NodeMqttBridgeResult> ValidateAsync(
        MqttEnvironment environment,
        string clientId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(environment, clientId, action: "validate");
        return await RunAsync(request, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeMqttBridgeResult> PublishAsync(
        MqttEnvironment environment,
        string clientId,
        string topic,
        byte[] payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);

        var request = BuildRequest(environment, clientId, action: "publish", topic: topic, payload: payload);
        return await RunAsync(request, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<NodeMqttListenerSession> StartListenerAsync(
        MqttEnvironment environment,
        string clientId,
        IReadOnlyList<string> topics,
        EventHandler<TabletInboundMessageEventArgs>? onMessage = null,
        EventHandler<string>? onLog = null,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        if (topics.Count == 0)
        {
            throw new InvalidOperationException("At least one subscription topic is required.");
        }

        var request = BuildRequest(environment, clientId, action: "listen", topics: topics);
        return NodeMqttListenerSession.StartAsync(
            this,
            request,
            connectTimeout ?? DefaultTimeout,
            onMessage,
            onLog,
            cancellationToken);
    }

    internal Process CreateListenerProcess(NodeMqttBridgeRequest request)
    {
        EnsureCertificateFiles(request);

        var nodeModules = Path.Combine(_workingDirectory, "node_modules", "mqtt");
        if (!Directory.Exists(nodeModules))
        {
            throw new InvalidOperationException(
                $"Node mqtt package not found at {nodeModules}. Run: cd NodeBridge && npm install");
        }

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _nodeExecutable,
                Arguments = $"\"{_scriptPath}\" --stdin",
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };
    }

    public async Task<NodeMqttBridgeResult> RunAsync(
        NodeMqttBridgeRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(_scriptPath))
        {
            return NodeMqttBridgeResult.Failed(
                $"Node bridge script not found: {_scriptPath}. Run 'npm install' in NodeBridge and rebuild.");
        }

        EnsureCertificateFiles(request);

        var nodeModules = Path.Combine(_workingDirectory, "node_modules", "mqtt");
        if (!Directory.Exists(nodeModules))
        {
            return NodeMqttBridgeResult.Failed(
                $"Node mqtt package not found at {nodeModules}. Run: cd NodeBridge && npm install");
        }

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);

        var startInfo = new ProcessStartInfo
        {
            FileName = _nodeExecutable,
            Arguments = $"\"{_scriptPath}\" --stdin",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        var sw = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                return NodeMqttBridgeResult.Failed("Failed to start Node.js process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.StandardInput.WriteAsync(payloadJson + "\n").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            var outText = stdout.ToString().Trim();
            var errText = stderr.ToString().Trim();

            if (process.ExitCode == 0)
            {
                return NodeMqttBridgeResult.Succeeded(sw.ElapsedMilliseconds, outText, errText);
            }

            return NodeMqttBridgeResult.Failed(
                $"Node bridge exited with code {process.ExitCode}.",
                sw.ElapsedMilliseconds,
                outText,
                errText);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort.
            }

            return NodeMqttBridgeResult.Failed(
                $"Node bridge timed out after {effectiveTimeout.TotalSeconds:0} seconds.",
                sw.ElapsedMilliseconds,
                stdout.ToString().Trim(),
                stderr.ToString().Trim());
        }
        catch (Exception ex)
        {
            return NodeMqttBridgeResult.Failed(
                ex.Message,
                sw.ElapsedMilliseconds,
                stdout.ToString().Trim(),
                stderr.ToString().Trim());
        }
    }

    private static NodeMqttBridgeRequest BuildRequest(
        MqttEnvironment environment,
        string clientId,
        string action,
        string? topic = null,
        byte[]? payload = null,
        IReadOnlyList<string>? topics = null)
    {
        environment.NormalizeHost();
        CertificatePathHelper.ResolveFromFolder(environment.Certificates);

        environment.Certificates.CaFile = CertificatePathHelper.Normalize(environment.Certificates.CaFile) ?? string.Empty;
        environment.Certificates.ClientCertificateFile =
            CertificatePathHelper.Normalize(environment.Certificates.ClientCertificateFile) ?? string.Empty;
        environment.Certificates.ClientKeyFile =
            CertificatePathHelper.Normalize(environment.Certificates.ClientKeyFile) ?? string.Empty;

        return new NodeMqttBridgeRequest
        {
            Action = action,
            Host = environment.Host,
            Port = environment.Port,
            // Always use the caller-provided id (e.g. abcdef-listen / abcdef-pub).
            // Overwriting with environment.ClientId caused listen+publish to share one id and
            // the broker kicked the listener on every Sync FULL publish.
            ClientId = clientId,
            Topic = topic,
            PayloadBase64 = payload is { Length: > 0 } ? Convert.ToBase64String(payload) : null,
            CaFile = environment.Certificates.CaFile,
            CertFile = environment.Certificates.ClientCertificateFile,
            KeyFile = environment.Certificates.ClientKeyFile,
            Username = string.IsNullOrWhiteSpace(environment.Username) ? null : environment.Username,
            Password = string.IsNullOrWhiteSpace(environment.Password) ? null : environment.Password,
            RejectUnauthorized = environment.SslSecure,
            CleanSession = environment.CleanSession,
            TimeoutMs = (int)DefaultTimeout.TotalMilliseconds,
            Topics = topics?.ToArray(),
        };
    }

    private static void EnsureCertificateFiles(NodeMqttBridgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CaFile))
        {
            throw new InvalidOperationException("CA file path is required for the Node MQTT bridge.");
        }

        if (string.IsNullOrWhiteSpace(request.CertFile))
        {
            throw new InvalidOperationException("Client certificate path is required for the Node MQTT bridge.");
        }

        if (string.IsNullOrWhiteSpace(request.KeyFile))
        {
            throw new InvalidOperationException("Client key path is required for the Node MQTT bridge.");
        }

        if (!File.Exists(request.CaFile))
        {
            throw new FileNotFoundException($"CA file not found: {request.CaFile}", request.CaFile);
        }

        if (!File.Exists(request.CertFile))
        {
            throw new FileNotFoundException($"Client certificate not found: {request.CertFile}", request.CertFile);
        }

        if (!File.Exists(request.KeyFile))
        {
            throw new FileNotFoundException($"Client key not found: {request.KeyFile}", request.KeyFile);
        }
    }

    public static bool ShouldUseNodeBridge(MqttEnvironment environment) =>
        environment.SslTls
        && (environment.UseNodeMqttBridge || OperatingSystem.IsWindows());
}

public sealed class NodeMqttBridgeRequest
{
    public string Action { get; init; } = "validate";
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string? Topic { get; init; }
    public string? PayloadBase64 { get; init; }
    public string? CaFile { get; init; }
    public string CertFile { get; init; } = string.Empty;
    public string KeyFile { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool RejectUnauthorized { get; init; }
    public bool CleanSession { get; init; } = true;
    public int TimeoutMs { get; init; }
    public string[]? Topics { get; init; }
}

public sealed class NodeMqttBridgeResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public long ElapsedMs { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;

    public static NodeMqttBridgeResult Succeeded(long elapsedMs, string stdout, string stderr) =>
        new() { Ok = true, ElapsedMs = elapsedMs, StandardOutput = stdout, StandardError = stderr };

    public static NodeMqttBridgeResult Failed(
        string error,
        long elapsedMs = 0,
        string stdout = "",
        string stderr = "") =>
        new() { Ok = false, Error = error, ElapsedMs = elapsedMs, StandardOutput = stdout, StandardError = stderr };
}
