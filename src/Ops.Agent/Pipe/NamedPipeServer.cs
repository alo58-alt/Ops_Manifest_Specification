using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Persistence;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Deployment;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Pipe;

public sealed class NamedPipeServer(
    NamedPipeSecurityFactory pipeSecurityFactory,
    AgentSnapshotCache snapshotCache,
    IOpsStateStore stateStore,
    OpsPathResolver pathResolver,
    JsonSerializerOptions jsonOptions,
    OperationCoordinator operationCoordinator,
    DeploymentEngine deploymentEngine,
    IOptions<OpsOptions> options,
    ILogger<NamedPipeServer> logger) : BackgroundService
{
    private readonly string _pipeName = options.Value.PipeName;
    private readonly string _hostId = pathResolver.Resolve().HostId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Named Pipe 查询端点已就绪：{PipeName}", _pipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = pipeSecurityFactory.Create();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Named Pipe 请求处理失败");
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
    {
        AgentResponse response;
        try
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var requestBytes = await ReadRequestAsync(pipe, readTimeout.Token);
            var request = JsonSerializer.Deserialize<AgentRequest>(requestBytes, jsonOptions)
                          ?? throw new JsonException("请求不能为空");
            using var processingTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            processingTimeout.CancelAfter(request.Command.Trim().ToLowerInvariant() switch
            {
                "deploy" => TimeSpan.FromMinutes(10),
                "operate" => TimeSpan.FromMinutes(2),
                _ => TimeSpan.FromSeconds(10)
            });
            response = await DispatchAsync(request, processingTimeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            response = Failure("unknown", "request_timeout", "请求读取或处理超时");
        }
        catch (JsonException exception)
        {
            response = Failure("unknown", "invalid_request", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            response = Failure("unknown", "invalid_request", exception.Message);
        }

        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, jsonOptions);
        if (responseBytes.Length > AgentProtocol.MaximumResponseBytes)
        {
            response = Failure(
                response.Command,
                "response_too_large",
                "盘点响应超过协议上限，后续版本需使用分页查询");
            responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, jsonOptions);
        }

        await pipe.WriteAsync(responseBytes, stoppingToken);
        await pipe.WriteAsync("\n"u8.ToArray(), stoppingToken);
        await pipe.FlushAsync(stoppingToken);
    }

    private async Task<AgentResponse> DispatchAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        var command = request.Command.Trim().ToLowerInvariant();
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.CreateVersion7().ToString()
            : request.CorrelationId;

        if (!string.Equals(
                request.ProtocolVersion,
                AgentProtocol.Version,
                StringComparison.Ordinal))
        {
            return Failure(
                command,
                "protocol_mismatch",
                $"仅支持 {AgentProtocol.Version}",
                correlationId);
        }

        object? data;
        switch (command)
        {
            case "ping":
                data = new
                {
                    hostId = _hostId,
                    agentVersion = typeof(NamedPipeServer).Assembly.GetName().Version?.ToString(),
                    mode = options.Value.EnableMutations ? "mutations-enabled" : "read-only"
                };
                break;
            case "inventory":
                data = snapshotCache.Read().Inventory;
                if (data is null)
                {
                    return Failure(command, "not_ready", "首次盘点尚未完成", correlationId);
                }

                break;
            case "catalog":
                data = snapshotCache.Read().Catalog;
                if (data is null)
                {
                    return Failure(command, "not_ready", "首次 Manifest 扫描尚未完成", correlationId);
                }

                break;
            case "projects":
                data = snapshotCache.Read().Projects;
                if (data is null)
                {
                    return Failure(command, "not_ready", "首次项目归属聚合尚未完成", correlationId);
                }

                break;
            case "audit":
                data = await stateStore.ReadRecentAuditEventsAsync(50, cancellationToken);
                break;
            case "operate":
                if (request.Data is null)
                {
                    return Failure(command, "invalid_operation", "operate 必须携带 data", correlationId);
                }

                var operationRequest = request.Data.Value.Deserialize<ComponentOperationRequest>(jsonOptions);
                if (operationRequest is null)
                {
                    return Failure(command, "invalid_operation", "无法解析操作请求", correlationId);
                }

                data = await operationCoordinator.ExecuteAsync(operationRequest, cancellationToken);
                break;
            case "deploy":
                if (request.Data is null)
                {
                    return Failure(command, "invalid_deployment", "deploy 必须携带 data", correlationId);
                }

                var deploymentRequest = request.Data.Value.Deserialize<DeploymentRequest>(jsonOptions);
                if (deploymentRequest is null)
                {
                    return Failure(command, "invalid_deployment", "无法解析部署请求", correlationId);
                }

                data = await deploymentEngine.ExecuteAsync(deploymentRequest, cancellationToken);
                break;
            default:
                return Failure(command, "unknown_command", "只允许 ping、inventory、catalog、projects、audit、operate、deploy", correlationId);
        }

        return new AgentResponse(
            AgentProtocol.Version,
            command,
            true,
            correlationId,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(data, jsonOptions));
    }

    private static AgentResponse Failure(
        string command,
        string errorCode,
        string errorMessage,
        string? correlationId = null) =>
        new(
            AgentProtocol.Version,
            command,
            false,
            correlationId ?? Guid.CreateVersion7().ToString(),
            DateTimeOffset.UtcNow,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage);

    private static async Task<byte[]> ReadRequestAsync(
        PipeStream pipe,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var bytesRead = await pipe.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                throw new InvalidDataException("客户端在发送完整请求前断开");
            }

            var newlineIndex = Array.IndexOf(chunk, (byte)'\n', 0, bytesRead);
            var bytesToWrite = newlineIndex >= 0 ? newlineIndex : bytesRead;
            await buffer.WriteAsync(chunk.AsMemory(0, bytesToWrite), cancellationToken);
            if (buffer.Length > AgentProtocol.MaximumRequestBytes)
            {
                throw new InvalidDataException("请求超过协议大小限制");
            }

            if (newlineIndex >= 0)
            {
                break;
            }
        }

        return buffer.ToArray();
    }
}
