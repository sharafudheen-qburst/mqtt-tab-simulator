using Microsoft.Data.Sqlite;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;

public sealed class AppStorageStore
{
    private readonly SimulatorDatabase _database;

    public AppStorageStore(SimulatorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT storage_value
                FROM app_storage
                WHERE storage_key = $key;
                """;
            command.Parameters.AddWithValue("$key", key.Trim());
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO app_storage (storage_key, storage_value, updated_at)
                VALUES ($key, $value, $updatedAt)
                ON CONFLICT(storage_key) DO UPDATE SET
                  storage_value = excluded.storage_value,
                  updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", key.Trim());
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        });
    }

    public void Delete(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM app_storage WHERE storage_key = $key;";
            command.Parameters.AddWithValue("$key", key.Trim());
            command.ExecuteNonQuery();
        });
    }
}
