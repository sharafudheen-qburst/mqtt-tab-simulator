namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public sealed class ConnectionAttemptLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    public void Info(string message) =>
        _entries.Add($"[{DateTime.UtcNow:HH:mm:ss.fff} UTC] {message}");

    public void Error(string message) =>
        _entries.Add($"[{DateTime.UtcNow:HH:mm:ss.fff} UTC] ERROR: {message}");

    public void Error(Exception ex, string? prefix = null)
    {
        var detail = ExceptionDetailFormatter.Format(ex);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            Error($"{prefix}: {detail}");
            return;
        }

        Error(detail);
    }
}
