using Microsoft.Data.Sqlite;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;

public sealed class SimulatorDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public SimulatorDatabase(string? databasePath = null)
    {
        var path = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(AppContext.BaseDirectory, "simulator.db")
            : databasePath.Trim();
        DatabasePath = path;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        InitializeSchema();
    }

    public string DatabasePath { get; }

    public SqliteConnection OpenConnection()
    {
        lock (_lock)
        {
            return new SqliteConnection(_connection.ConnectionString);
        }
    }

    public void Execute(Action<SqliteConnection> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_lock)
        {
            action(_connection);
        }
    }

    public T Execute<T>(Func<SqliteConnection, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_lock)
        {
            return action(_connection);
        }
    }

    private void InitializeSchema()
    {
        Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS inbound_messages (
                  sequence INTEGER PRIMARY KEY,
                  received_at TEXT NOT NULL,
                  topic TEXT NOT NULL,
                  payload_length INTEGER NOT NULL,
                  retained INTEGER NOT NULL,
                  decoded_summary TEXT NOT NULL,
                  payload_hex TEXT NOT NULL,
                  event_type TEXT,
                  equipment_id TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_inbound_received_at
                  ON inbound_messages(received_at DESC);

                CREATE TABLE IF NOT EXISTS app_storage (
                  storage_key TEXT PRIMARY KEY,
                  storage_value TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS devices (
                  device_id TEXT PRIMARY KEY COLLATE NOCASE,
                  equipment_id TEXT NOT NULL,
                  name TEXT NOT NULL DEFAULT '',
                  certificate_folder TEXT NOT NULL DEFAULT '',
                  updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS outbound_messages (
                  sequence INTEGER PRIMARY KEY,
                  published_at TEXT NOT NULL,
                  topic TEXT NOT NULL,
                  payload_length INTEGER NOT NULL,
                  retained INTEGER NOT NULL,
                  decoded_summary TEXT NOT NULL,
                  payload_hex TEXT NOT NULL,
                  event_type TEXT,
                  equipment_id TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_outbound_published_at
                  ON outbound_messages(published_at DESC);
                """;
            command.ExecuteNonQuery();

            EnsureColumn(connection, "inbound_messages", "equipment_id", "TEXT");
            EnsureColumn(connection, "devices", "certificate_folder", "TEXT NOT NULL DEFAULT ''");
        });
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string typeSql)
    {
        using var list = connection.CreateCommand();
        list.CommandText = $"PRAGMA table_info({table});";
        using var reader = list.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql};";
        alter.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }
}
