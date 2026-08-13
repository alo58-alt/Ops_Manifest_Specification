using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.SessionAgent;

public sealed class InteractiveControlServer(
    InteractiveClaimReader claims,
    InteractiveProcessManager processes,
    IOptions<SessionAgentOptions> options,
    JsonSerializerOptions jsonOptions,
    ILogger<InteractiveControlServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe(options.Value.PipeName);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                var request = JsonSerializer.Deserialize<InteractiveAppControlRequest>(await ReadLineAsync(pipe, stoppingToken), jsonOptions);
                var response = await DispatchAsync(request, stoppingToken);
                await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(response, jsonOptions), stoppingToken);
                await pipe.WriteAsync("\n"u8.ToArray(), stoppingToken);
                await pipe.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "交互会话控制请求失败"); }
        }
    }

    private async Task<InteractiveAppControlResponse> DispatchAsync(
        InteractiveAppControlRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.ProtocolVersion != InteractiveSessionProtocol.ControlVersion ||
            request.RequestId.Length is < 1 or > 120)
            return Response(request, false, "invalid_request", "交互程序控制请求无效");
        var matches = (await claims.ReadAsync(cancellationToken)).Where(claim =>
            claim.ProjectId == request.ProjectId && claim.Environment == request.Environment &&
            claim.ComponentId == request.ComponentId).ToArray();
        if (matches.Length != 1) return Response(request, false, "claim_not_unique", "当前用户会话中没有唯一的交互程序声明");
        var result = await processes.ExecuteAsync(matches[0], request, cancellationToken);
        return Response(request, result.Success, result.ErrorCode, result.Detail);
    }

    private static InteractiveAppControlResponse Response(
        InteractiveAppControlRequest? request, bool success, string? code, string detail) =>
        new(InteractiveSessionProtocol.ControlVersion, request?.RequestId ?? Guid.CreateVersion7().ToString(), success, code, detail);

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sidType, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
            security.AddAccessRule(new PipeAccessRule(identity.User,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, 64 * 1024, 64 * 1024, security);
    }

    private static async Task<byte[]> ReadLineAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await pipe.ReadAsync(chunk, cancellationToken);
            if (count == 0) throw new InvalidDataException("请求未完成");
            var newline = Array.IndexOf(chunk, (byte)'\n', 0, count);
            await buffer.WriteAsync(chunk.AsMemory(0, newline >= 0 ? newline : count), cancellationToken);
            if (buffer.Length > AgentProtocol.MaximumRequestBytes) throw new InvalidDataException("请求超过限制");
            if (newline >= 0) return buffer.ToArray();
        }
    }
}
