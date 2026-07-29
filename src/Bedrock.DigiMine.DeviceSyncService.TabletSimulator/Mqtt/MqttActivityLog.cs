namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

/// <summary>In-memory ring buffer of MQTT lifecycle events for the global UI log.</summary>
public sealed class MqttActivityLog
{
    public const int DefaultCapacity = 1000;

    private readonly object _lock = new();
    private readonly LinkedList<MqttActivityLogEntry> _entries = new();
    private readonly int _capacity;

    public MqttActivityLog(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    public event EventHandler<MqttActivityLogEntry>? EntryAdded;

    public void Info(string message, string? deviceId = null) =>
        Add("info", message, deviceId);

    public void Error(string message, string? deviceId = null) =>
        Add("error", message, deviceId);

    public void Add(string level, string message, string? deviceId = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new MqttActivityLogEntry(
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant(),
            message.Trim(),
            string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim());

        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > _capacity)
            {
                _entries.RemoveFirst();
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void AddRange(IEnumerable<string> lines, string level = "info", string? deviceId = null)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Add(level, line, deviceId);
        }
    }

    public IReadOnlyList<MqttActivityLogEntry> GetRecent(int limit = 200)
    {
        if (limit <= 0)
        {
            limit = 200;
        }

        lock (_lock)
        {
            return _entries.Reverse().Take(limit).Reverse().ToArray();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}

public sealed record MqttActivityLogEntry(
    DateTimeOffset At,
    string Level,
    string Message,
    string? DeviceId);
