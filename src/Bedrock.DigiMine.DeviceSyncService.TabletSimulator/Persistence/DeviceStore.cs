using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using Microsoft.Data.Sqlite;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;

public sealed class DeviceStore
{
    private readonly SimulatorDatabase _database;

    public DeviceStore(SimulatorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public IReadOnlyList<DeviceEntry> ListAll() =>
        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT device_id, equipment_id, name, certificate_folder
                FROM devices
                ORDER BY name COLLATE NOCASE, device_id COLLATE NOCASE;
                """;

            var results = new List<DeviceEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new DeviceEntry
                {
                    DeviceId = reader.GetString(0),
                    EquipmentId = reader.GetString(1),
                    Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    CertificateFolder = reader.FieldCount > 3 && !reader.IsDBNull(3)
                        ? reader.GetString(3)
                        : string.Empty,
                });
            }

            return results;
        });

    public int Count() =>
        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM devices;";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

    public void Upsert(DeviceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.DeviceId);

        _database.Execute(connection => UpsertCore(connection, entry));
    }

    public void ReplaceAll(IEnumerable<DeviceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM devices;";
                clear.ExecuteNonQuery();
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.DeviceId))
                {
                    continue;
                }

                UpsertCore(connection, entry, transaction);
            }

            transaction.Commit();
        });
    }

    public bool UpdateName(string deviceId, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var normalizedName = name?.Trim() ?? string.Empty;

        return _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE devices
                SET name = $name, updated_at = $updatedAt
                WHERE device_id = $deviceId COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$name", normalizedName);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$deviceId", deviceId.Trim());
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>
    /// Seeds SQLite from config when empty; otherwise merges (keeps SQLite name when config name is blank),
    /// then reloads the config device list from SQLite.
    /// </summary>
    public void SyncWithConfig(SimulatorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.EnsureDevicesMigrated();

        var fromDb = ListAll()
            .ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);

        if (fromDb.Count == 0)
        {
            config.MigrateEnvironmentCertificatesToDevices();
            if (config.Devices.Count > 0)
            {
                ReplaceAll(config.Devices);
            }
        }
        else
        {
            foreach (var entry in config.Devices)
            {
                if (fromDb.TryGetValue(entry.DeviceId, out var existing))
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)
                        && !string.IsNullOrWhiteSpace(existing.Name))
                    {
                        entry.Name = existing.Name;
                    }

                    if (string.IsNullOrWhiteSpace(entry.CertificateFolder)
                        && !string.IsNullOrWhiteSpace(existing.CertificateFolder))
                    {
                        entry.CertificateFolder = existing.CertificateFolder;
                    }
                }
            }

            config.MigrateEnvironmentCertificatesToDevices();
            ReplaceAll(config.Devices);
        }

        var loaded = ListAll();
        if (loaded.Count == 0)
        {
            return;
        }

        config.Devices.Clear();
        config.Devices.AddRange(loaded);

        if (!string.IsNullOrWhiteSpace(config.Device.DeviceId)
            && config.FindDevice(config.Device.DeviceId) is { } active)
        {
            config.Device.DeviceId = active.DeviceId;
            config.Device.EquipmentId = active.EquipmentId;
        }
        else
        {
            config.SelectDevice(loaded[0].DeviceId);
        }
    }

    private static void UpsertCore(
        SqliteConnection connection,
        DeviceEntry entry,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.CommandText =
            """
            INSERT INTO devices (device_id, equipment_id, name, certificate_folder, updated_at)
            VALUES ($deviceId, $equipmentId, $name, $certificateFolder, $updatedAt)
            ON CONFLICT(device_id) DO UPDATE SET
              equipment_id = excluded.equipment_id,
              name = excluded.name,
              certificate_folder = excluded.certificate_folder,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$deviceId", entry.DeviceId.Trim());
        command.Parameters.AddWithValue(
            "$equipmentId",
            string.IsNullOrWhiteSpace(entry.EquipmentId)
                ? Guid.NewGuid().ToString()
                : entry.EquipmentId.Trim());
        command.Parameters.AddWithValue("$name", entry.Name?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue(
            "$certificateFolder",
            entry.CertificateFolder?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
