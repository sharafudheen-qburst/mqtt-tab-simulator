using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;
using Microsoft.Data.Sqlite;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;

/// <summary>
/// Persists device→service uplink publishes. Uses the same message shape as inbound.
/// </summary>
public sealed class OutboundMessageStore
{
    private const int MaxStoredMessages = 5000;
    private readonly SimulatorDatabase _database;

    public OutboundMessageStore(SimulatorDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public long GetMaxSequence() =>
        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM outbound_messages;";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

    public void Save(TabletInboundMessage message)
    {
        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR REPLACE INTO outbound_messages (
                  sequence, published_at, topic, payload_length, retained,
                  decoded_summary, payload_hex, event_type, equipment_id
                ) VALUES (
                  $sequence, $publishedAt, $topic, $payloadLength, $retained,
                  $decodedSummary, $payloadHex, $eventType, $equipmentId
                );
                """;
            BindMessage(command, message);
            command.ExecuteNonQuery();
            TrimExcess(connection);
        });
    }

    public IReadOnlyList<TabletInboundMessage> GetRecent(int limit = 500)
    {
        if (limit <= 0)
        {
            return [];
        }

        return _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT sequence, published_at, topic, payload_length, retained,
                       decoded_summary, payload_hex, event_type, equipment_id
                FROM outbound_messages
                ORDER BY sequence DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<TabletInboundMessage>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadMessage(reader));
            }

            results.Reverse();
            return results;
        });
    }

    public TabletInboundMessage? GetBySequence(long sequence) =>
        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT sequence, published_at, topic, payload_length, retained,
                       decoded_summary, payload_hex, event_type, equipment_id
                FROM outbound_messages
                WHERE sequence = $sequence
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$sequence", sequence);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMessage(reader) : null;
        });

    public int Count() =>
        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM outbound_messages;";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

    public void Clear()
    {
        _database.Execute(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM outbound_messages;";
            command.ExecuteNonQuery();
        });
    }

    private static void TrimExcess(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM outbound_messages
            WHERE sequence NOT IN (
              SELECT sequence
              FROM outbound_messages
              ORDER BY sequence DESC
              LIMIT $maxCount
            );
            """;
        command.Parameters.AddWithValue("$maxCount", MaxStoredMessages);
        command.ExecuteNonQuery();
    }

    private static void BindMessage(SqliteCommand command, TabletInboundMessage message)
    {
        command.Parameters.AddWithValue("$sequence", message.Sequence);
        command.Parameters.AddWithValue("$publishedAt", message.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("$topic", message.Topic);
        command.Parameters.AddWithValue("$payloadLength", message.PayloadLength);
        command.Parameters.AddWithValue("$retained", message.Retained ? 1 : 0);
        command.Parameters.AddWithValue("$decodedSummary", message.DecodedSummary);
        command.Parameters.AddWithValue("$payloadHex", message.PayloadHex);
        command.Parameters.AddWithValue("$eventType", (object?)message.EventType ?? DBNull.Value);
        command.Parameters.AddWithValue("$equipmentId", (object?)message.EquipmentId ?? DBNull.Value);
    }

    private static TabletInboundMessage ReadMessage(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4) != 0,
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null);
}
