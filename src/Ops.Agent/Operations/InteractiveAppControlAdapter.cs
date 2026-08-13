using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using CompanyOps.Agent.Inventory;
using CompanyOps.Contracts;

namespace CompanyOps.Agent.Operations;

public interface IInteractiveSessionControlBridge
{
    Task<AdapterExecutionResult> ExecuteAsync(string pipeName, InteractiveAppControlRequest request, CancellationToken cancellationToken);
}

public sealed class NamedPipeInteractiveSessionControlBridge(JsonSerializerOptions jsonOptions) : IInteractiveSessionControlBridge
{
    public async Task<AdapterExecutionResult> ExecuteAsync(
        string pipeName, InteractiveAppControlRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) return new(false, "Session Agent 控制管道未配置");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(70));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, jsonOptions), timeout.Token);
            await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token);
            var response = line is null ? null : JsonSerializer.Deserialize<InteractiveAppControlResponse>(line, jsonOptions);
            return response?.Success == true ? new(true, response.Detail) : new(false, response?.Detail ?? "Session Agent 响应无效");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return new(false, $"Session Agent 不可用：{exception.Message}");
        }
    }
}

public sealed class InteractiveAppControlAdapter(
    IInteractiveSessionControlBridge bridge,
    IInteractiveSessionClaimProvider claims) : IComponentControlAdapter
{
    public string Kind => "interactiveApp";

    public async Task<AdapterExecutionResult> ExecuteAsync(
        ComponentControlTarget target, ComponentOperationAction action, CancellationToken cancellationToken)
    {
        var matches = (await claims.GetClaimsAsync(cancellationToken)).Where(claim =>
            claim.ProjectId == target.ProjectId && claim.Environment == target.Environment &&
            claim.ComponentId == target.ComponentId && claim.BindingError is null).ToArray();
        if (matches.Length != 1) return new(false, "交互程序声明不唯一或不完整");
        var claim = matches[0];
        if (claim.ControlPipeName is null || claim.ExpectedExecutable is null || claim.ExpectedWorkingDirectory is null)
            return new(false, "交互会话绑定不完整");
        return await bridge.ExecuteAsync(claim.ControlPipeName,
            new InteractiveAppControlRequest(
                InteractiveSessionProtocol.ControlVersion, Guid.CreateVersion7().ToString(),
                target.ProjectId, target.Environment, target.ComponentId,
                claim.ExpectedExecutable, claim.ExpectedWorkingDirectory, claim.ExpectedArguments, action),
            cancellationToken);
    }
}
