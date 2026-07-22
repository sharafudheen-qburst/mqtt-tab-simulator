using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public sealed class NodeMqttListenerSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private Task _readLoopTask = Task.CompletedTask;
    private bool _connected;

    private NodeMqttListenerSession(Process process)
    {
        _process = process;
    }

    public event EventHandler<TabletInboundMessageEventArgs>? MessageReceived;
    public event EventHandler<string>? LogReceived;

    public bool IsConnected => _connected && !_process.HasExited;

    public static async Task<NodeMqttListenerSession> StartAsync(
        NodeMqttBridgeService bridge,
        NodeMqttBridgeRequest request,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(request);

        var process = bridge.CreateListenerProcess(request);
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Node MQTT listener process.");
        }

        process.BeginErrorReadLine();
        var session = new NodeMqttListenerSession(process);

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                session.LogReceived?.Invoke(session, e.Data);
            }
        };

        // NDJSON: first line is the listen config; stdin stays open for {"cmd":"stop"}.
        await process.StandardInput.WriteAsync(
            JsonSerializer.Serialize(request, JsonOptions) + "\n").ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(connectTimeout);

        session._readLoopTask = session.ReadStdoutLoopAsync(session._cts.Token);
        await session.WaitForConnectedAsync(timeoutCts.Token).ConfigureAwait(false);

        return session;
    }

    private async Task WaitForConnectedAsync(CancellationToken cancellationToken)
    {
        while (!_connected && !cancellationToken.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Node MQTT listener exited before connect (code {_process.ExitCode}).");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task ReadStdoutLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleEventLine(line);
            }

            if (!_process.HasExited)
            {
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_process.HasExited && _process.ExitCode != 0)
            {
                LogReceived?.Invoke(this, $"Node listener exited (code {_process.ExitCode})");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(this, $"Listener read error: {ex.Message}");
        }
    }

    private void HandleEventLine(string line)
    {
        NodeBridgeEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<NodeBridgeEvent>(line, JsonOptions);
        }
        catch (JsonException)
        {
            LogReceived?.Invoke(this, line);
            return;
        }

        if (evt is null)
        {
            return;
        }

        switch (evt.Type?.ToLowerInvariant())
        {
            case "connected":
                _connected = true;
                LogReceived?.Invoke(this, "Node listener connected to broker");
                break;
            case "subscribed":
                LogReceived?.Invoke(this, $"Subscribed: {evt.Topic}");
                break;
            case "message" when !string.IsNullOrWhiteSpace(evt.Topic):
                var payload = string.IsNullOrWhiteSpace(evt.PayloadBase64)
                    ? []
                    : Convert.FromBase64String(evt.PayloadBase64);
                var receivedAt = DateTimeOffset.TryParse(evt.ReceivedAt, out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;
                var decoded = TabletMqttClient.DecodeInbound(evt.Topic, payload);
                var inbound = new TabletInboundMessage(
                    0,
                    receivedAt,
                    evt.Topic,
                    payload.Length,
                    evt.Retain,
                    decoded.Summary,
                    payload.Length > 0 ? Convert.ToHexString(payload) : string.Empty,
                    decoded.EventType,
                    decoded.EquipmentId);
                MessageReceived?.Invoke(this, new TabletInboundMessageEventArgs(inbound));
                break;
            case "error":
                LogReceived?.Invoke(this, evt.Message ?? "Node listener error");
                break;
            case "offline":
            case "closed":
                _connected = false;
                LogReceived?.Invoke(this, $"Node listener {evt.Type}");
                break;
            case "reconnecting":
                LogReceived?.Invoke(this, "Node listener reconnecting...");
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        // Prefer a clean MQTT unsubscribe + disconnect via stdin stop command.
        try
        {
            if (!_process.HasExited && _process.StandardInput.BaseStream.CanWrite)
            {
                await _process.StandardInput.WriteAsync("{\"cmd\":\"stop\"}\n").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                _process.StandardInput.Close();
            }
        }
        catch
        {
            // Best effort — fall through to wait / kill.
        }

        try
        {
            if (!_process.HasExited)
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for graceful stop.
        }
        catch
        {
            // Best effort.
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }

        try
        {
            await _readLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }

        _process.Dispose();
        _cts.Dispose();
    }

    private sealed class NodeBridgeEvent
    {
        public string? Type { get; init; }
        public string? Topic { get; init; }
        public string? PayloadBase64 { get; init; }
        public bool Retain { get; init; }
        public string? ReceivedAt { get; init; }
        public string? Message { get; init; }
        public string? ClientId { get; init; }
    }
}
