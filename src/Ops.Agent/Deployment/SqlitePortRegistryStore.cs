using CompanyOps.Contracts;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace CompanyOps.Agent.Deployment;

public sealed class SqlitePortRegistryStore(OpsPathResolver pathResolver) : IPortRegistryStore
{
    private readonly ResolvedOpsPaths _paths = pathResolver.Resolve();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS port_reservations (
                protocol TEXT NOT NULL,
                address TEXT NOT NULL,
                port INTEGER NOT NULL,
                project_id TEXT NOT NULL,
                environment TEXT NOT NULL,
                component_id TEXT NOT NULL,
                port_id TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                state TEXT NOT NULL,
                reserved_at TEXT NOT NULL,
                PRIMARY KEY(protocol, address, port)
            );
            CREATE INDEX IF NOT EXISTS ix_port_reservations_operation
            ON port_reservations(operation_id);
            CREATE TABLE IF NOT EXISTS port_reservation_operations (
                operation_id TEXT PRIMARY KEY,
                state TEXT NOT NULL,
                request_fingerprint TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO port_reservation_operations(operation_id, state, request_fingerprint, created_at)
            SELECT operation_id,
                   CASE WHEN MAX(CASE WHEN state = 'active' THEN 1 ELSE 0 END) = 1
                        THEN 'committed' ELSE 'reserved' END,
                   'legacy/' || operation_id,
                   MIN(reserved_at)
            FROM port_reservations
            GROUP BY operation_id;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PortReservationResult> ReserveAsync(
        IReadOnlyList<PortReservationRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return new PortReservationResult(true, []);
        }

        var validation = ValidateBatch(requests);
        if (validation is not null)
        {
            return new PortReservationResult(false, [], "invalid_port_request", validation);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            System.Data.IsolationLevel.Serializable,
            deferred: false);
        var operationId = requests[0].OperationId;
        var requestFingerprint = RequestFingerprint(requests);
        await using (var register = connection.CreateCommand())
        {
            register.Transaction = transaction;
            register.CommandText =
                """
                INSERT INTO port_reservation_operations(operation_id, state, request_fingerprint, created_at)
                VALUES($operation_id, 'reserved', $request_fingerprint, $created_at)
                ON CONFLICT(operation_id) DO NOTHING;
                """;
            register.Parameters.AddWithValue("$operation_id", operationId);
            register.Parameters.AddWithValue("$request_fingerprint", requestFingerprint);
            register.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
            if (await register.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await using var stateQuery = connection.CreateCommand();
                stateQuery.Transaction = transaction;
                stateQuery.CommandText =
                    "SELECT state, request_fingerprint FROM port_reservation_operations WHERE operation_id = $operation_id;";
                stateQuery.Parameters.AddWithValue("$operation_id", operationId);
                await using var stateReader = await stateQuery.ExecuteReaderAsync(cancellationToken);
                if (!await stateReader.ReadAsync(cancellationToken) ||
                    stateReader.GetString(0) != "reserved" ||
                    !string.Equals(stateReader.GetString(1), requestFingerprint, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new PortReservationResult(
                        false,
                        [],
                        "operation_id_reused",
                        "操作 ID 已经完成或释放，不允许复用");
                }
            }
        }
        foreach (var request in requests)
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText =
                """
                SELECT address, project_id, environment, component_id, port_id, state, operation_id
                FROM port_reservations
                WHERE protocol = $protocol AND port = $port AND state IN ('reserved', 'active');
                """;
            query.Parameters.AddWithValue("$protocol", request.Protocol.ToLowerInvariant());
            query.Parameters.AddWithValue("$port", request.Port);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var address = reader.GetString(0);
                var sameOwner =
                    string.Equals(reader.GetString(1), request.ProjectId, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(2), request.Environment, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(3), request.ComponentId, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(4), request.PortId, StringComparison.Ordinal);
                var anotherReservation = reader.GetString(5) == "reserved" &&
                    !string.Equals(reader.GetString(6), request.OperationId, StringComparison.Ordinal);
                if (AddressesOverlap(address, request.Address) && (!sameOwner || anotherReservation))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new PortReservationResult(
                        false,
                        [],
                        "port_conflict",
                        $"{request.Protocol}/{request.Address}:{request.Port} 与已有登记冲突");
                }
            }

            await reader.DisposeAsync();
            // An active row belongs to the still-running release. Keep it active so that
            // releasing a failed update cannot erase the previous release's ownership.
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO port_reservations(
                    protocol, address, port, project_id, environment,
                    component_id, port_id, operation_id, state, reserved_at)
                VALUES(
                    $protocol, $address, $port, $project_id, $environment,
                    $component_id, $port_id, $operation_id, 'reserved', $reserved_at)
                ON CONFLICT(protocol, address, port) DO UPDATE SET
                    operation_id = CASE WHEN port_reservations.state = 'active'
                        THEN port_reservations.operation_id ELSE excluded.operation_id END,
                    state = CASE WHEN port_reservations.state = 'active' THEN 'active' ELSE 'reserved' END,
                    reserved_at = CASE WHEN port_reservations.state = 'active'
                        THEN port_reservations.reserved_at ELSE excluded.reserved_at END
                WHERE project_id = excluded.project_id
                  AND environment = excluded.environment
                  AND component_id = excluded.component_id
                  AND port_id = excluded.port_id;
                """;
            AddParameters(insert, request);
            var affected = await insert.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PortReservationResult(false, [], "port_conflict", "端口被其他资源占用");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new PortReservationResult(true, requests);
    }

    public async Task ReleaseOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            System.Data.IsolationLevel.Serializable,
            deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM port_reservations WHERE operation_id = $operation_id AND state = 'reserved';";
        command.Parameters.AddWithValue("$operation_id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetOperationStateAsync(connection, transaction, operationId, "released", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CommitOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            System.Data.IsolationLevel.Serializable,
            deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE port_reservations SET state = 'active' WHERE operation_id = $operation_id AND state = 'reserved';";
        command.Parameters.AddWithValue("$operation_id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetOperationStateAsync(connection, transaction, operationId, "committed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RollbackOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            System.Data.IsolationLevel.Serializable,
            deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Existing active ownership keeps its original operation_id during ReserveAsync. This
        // therefore removes only rows introduced by the failed operation, even after commit.
        command.CommandText = "DELETE FROM port_reservations WHERE operation_id = $operation_id;";
        command.Parameters.AddWithValue("$operation_id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetOperationStateAsync(connection, transaction, operationId, "rolled_back", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SetOperationStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE port_reservation_operations SET state = $state WHERE operation_id = $operation_id;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$operation_id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string RequestFingerprint(IReadOnlyList<PortReservationRequest> requests)
    {
        var canonical = string.Join('\n', requests
            .Select(request => string.Join('\u001f',
                request.Protocol.ToLowerInvariant(),
                request.Address,
                request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.ProjectId,
                request.Environment,
                request.ComponentId,
                request.PortId))
            .OrderBy(static value => value, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? ValidateBatch(IReadOnlyList<PortReservationRequest> requests)
    {
        foreach (var request in requests)
        {
            if (request.Protocol is not ("tcp" or "udp") ||
                request.Address is not ("127.0.0.1" or "0.0.0.0" or "::1" or "::") ||
                request.Port is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(request.OperationId))
            {
                return "协议、地址、端口或操作 ID 无效";
            }
        }

        for (var index = 0; index < requests.Count; index++)
        {
            for (var other = index + 1; other < requests.Count; other++)
            {
                if (requests[index].Protocol == requests[other].Protocol &&
                    requests[index].Port == requests[other].Port &&
                    AddressesOverlap(requests[index].Address, requests[other].Address))
                {
                    return "同一批请求内部存在端口冲突";
                }
            }
        }

        return null;
    }

    private static bool AddressesOverlap(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var leftV4 = left is "127.0.0.1" or "0.0.0.0";
        var rightV4 = right is "127.0.0.1" or "0.0.0.0";
        if (leftV4 && rightV4)
        {
            return left == "0.0.0.0" || right == "0.0.0.0";
        }

        var leftV6 = left is "::1" or "::";
        var rightV6 = right is "::1" or "::";
        return leftV6 && rightV6 && (left == "::" || right == "::");
    }

    private static void AddParameters(SqliteCommand command, PortReservationRequest request)
    {
        command.Parameters.AddWithValue("$protocol", request.Protocol);
        command.Parameters.AddWithValue("$address", request.Address);
        command.Parameters.AddWithValue("$port", request.Port);
        command.Parameters.AddWithValue("$project_id", request.ProjectId);
        command.Parameters.AddWithValue("$environment", request.Environment);
        command.Parameters.AddWithValue("$component_id", request.ComponentId);
        command.Parameters.AddWithValue("$port_id", request.PortId);
        command.Parameters.AddWithValue("$operation_id", request.OperationId);
        command.Parameters.AddWithValue("$reserved_at", DateTimeOffset.UtcNow.ToString("O"));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.StateDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
