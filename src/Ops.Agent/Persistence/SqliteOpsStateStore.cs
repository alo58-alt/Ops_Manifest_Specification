using System.Text.Json;
using CompanyOps.Contracts;
using Microsoft.Data.Sqlite;

namespace CompanyOps.Agent.Persistence;

public sealed class SqliteOpsStateStore(
    OpsPathResolver pathResolver,
    JsonSerializerOptions jsonOptions) : IOpsStateStore
{
    private readonly ResolvedOpsPaths _paths = pathResolver.Resolve();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_info(version, applied_at)
            VALUES (1, $applied_at);

            CREATE TABLE IF NOT EXISTS inventory_snapshots (
                snapshot_id TEXT NOT NULL PRIMARY KEY,
                host_id TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_inventory_snapshots_observed_at
            ON inventory_snapshots(observed_at DESC);

            CREATE TABLE IF NOT EXISTS audit_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                category TEXT NOT NULL,
                action TEXT NOT NULL,
                outcome TEXT NOT NULL,
                detail TEXT NULL,
                data_json TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_audit_events_occurred_at
            ON audit_events(occurred_at DESC);
            """;
        command.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureAuditDataColumnAsync(connection, cancellationToken);
    }

    public async Task SaveInventorySnapshotAsync(
        InventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var snapshotId = Guid.CreateVersion7().ToString();
        var payload = JsonSerializer.Serialize(snapshot, jsonOptions);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText =
            """
            INSERT INTO inventory_snapshots(snapshot_id, host_id, observed_at, payload_json)
            VALUES ($snapshot_id, $host_id, $observed_at, $payload_json);
            """;
        insert.Parameters.AddWithValue("$snapshot_id", snapshotId);
        insert.Parameters.AddWithValue("$host_id", snapshot.HostId);
        insert.Parameters.AddWithValue("$observed_at", snapshot.ObservedAt.ToString("O"));
        insert.Parameters.AddWithValue("$payload_json", payload);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var prune = connection.CreateCommand();
        prune.Transaction = (SqliteTransaction)transaction;
        prune.CommandText =
            """
            DELETE FROM inventory_snapshots
            WHERE snapshot_id NOT IN (
                SELECT snapshot_id
                FROM inventory_snapshots
                ORDER BY observed_at DESC
                LIMIT 100
            );
            """;
        await prune.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AppendAuditEventAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audit_events(
                event_id,
                occurred_at,
                category,
                action,
                outcome,
                detail,
                data_json)
            VALUES (
                $event_id,
                $occurred_at,
                $category,
                $action,
                $outcome,
                $detail,
                $data_json);
            """;
        command.Parameters.AddWithValue("$event_id", auditEvent.EventId);
        command.Parameters.AddWithValue("$occurred_at", auditEvent.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$category", auditEvent.Category);
        command.Parameters.AddWithValue("$action", auditEvent.Action);
        command.Parameters.AddWithValue("$outcome", auditEvent.Outcome);
        command.Parameters.AddWithValue(
            "$detail",
            auditEvent.Detail is null ? DBNull.Value : auditEvent.Detail);
        command.Parameters.AddWithValue(
            "$data_json",
            auditEvent.Data is null ? DBNull.Value : auditEvent.Data.Value.GetRawText());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadRecentAuditEventsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, 200);
        var events = new List<AuditEvent>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, occurred_at, category, action, outcome, detail, data_json
            FROM audit_events
            ORDER BY occurred_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", boundedLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(
                new AuditEvent(
                    reader.GetString(0),
                    DateTimeOffset.Parse(
                        reader.GetString(1),
                        System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6)
                        ? null
                        : JsonSerializer.Deserialize<JsonElement>(reader.GetString(6))));
        }

        return events;
    }

    private static async Task EnsureAuditDataColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(audit_events);";
        var hasDataColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "data_json", StringComparison.OrdinalIgnoreCase))
                {
                    hasDataColumn = true;
                    break;
                }
            }
        }
        if (hasDataColumn)
        {
            return;
        }

        await using var migrate = connection.CreateCommand();
        migrate.CommandText = "ALTER TABLE audit_events ADD COLUMN data_json TEXT NULL;";
        await migrate.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.StateDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
