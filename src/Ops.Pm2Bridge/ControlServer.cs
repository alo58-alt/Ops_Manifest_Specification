using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Pm2Bridge;

public sealed class ControlServer(
    Pm2CliRunner runner,
    IOptions<BridgeOptions> options,
    JsonSerializerOptions jsonOptions,
    ILogger<ControlServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe(options.Value.PipeName);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                var requestBytes = await ReadLineAsync(pipe, stoppingToken);
                var request = JsonSerializer.Deserialize<Pm2BridgeControlRequest>(requestBytes, jsonOptions);
                var response = await DispatchAsync(request, stoppingToken);
                await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(response, jsonOptions), stoppingToken);
                await pipe.WriteAsync("\n"u8.ToArray(), stoppingToken);
                await pipe.FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "PM2 owner bridge 请求失败");
            }
        }
    }

    private async Task<Pm2BridgeControlResponse> DispatchAsync(
        Pm2BridgeControlRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.ProtocolVersion != Pm2BridgeProtocol.Version ||
            request.PmId < 0 || request.Name.Length is < 1 or > 120 ||
            request.ExpectedCwd.Length is < 3 or > 500 || request.ExpectedScript.Length is < 3 or > 500)
        {
            return new Pm2BridgeControlResponse(
                Pm2BridgeProtocol.Version,
                request?.RequestId ?? Guid.CreateVersion7().ToString(),
                false,
                "invalid_request",
                "控制请求无效");
        }

        var result = await runner.ControlAsync(request, cancellationToken);
        return new Pm2BridgeControlResponse(
            Pm2BridgeProtocol.Version,
            request.RequestId,
            result.Success,
            result.Success ? null : "ownership_or_cli_failed",
            result.Detail);
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sidType in new[]
                 {
                     WellKnownSidType.LocalSystemSid,
                     WellKnownSidType.BuiltinAdministratorsSid
                 })
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(sidType, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                identity.User,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            2,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            64 * 1024,
            64 * 1024,
            security);
    }

    private static async Task<byte[]> ReadLineAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await pipe.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                throw new InvalidDataException("请求未完成");
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, count);
            await buffer.WriteAsync(chunk.AsMemory(0, newline >= 0 ? newline : count), cancellationToken);
            if (buffer.Length > AgentProtocol.MaximumRequestBytes)
            {
                throw new InvalidDataException("请求超过限制");
            }

            if (newline >= 0)
            {
                return buffer.ToArray();
            }
        }
    }
}
