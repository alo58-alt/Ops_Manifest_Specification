using CompanyOps.Contracts;
using Microsoft.Data.Sqlite;

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
        foreach (var request in requests)
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText =
                """
                SELECT address, project_id, environment, component_id, port_id
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
                if (AddressesOverlap(address, request.Address) && !sameOwner)
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
                    operation_id = excluded.operation_id,
                    state = 'reserved',
                    reserved_at = excluded.reserved_at
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
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM port_reservations WHERE operation_id = $operation_id AND state = 'reserved';";
        command.Parameters.AddWithValue("$operation_id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CommitOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE port_reservations SET state = 'active' WHERE operation_id = $operation_id AND state = 'reserved';";
        command.Parameters.AddWithValue("$operation_id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
