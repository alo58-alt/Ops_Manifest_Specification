using System.IO.Pipes;
using System.Text.Json;
using CompanyOps.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyOps.Console;

public sealed class AgentPipeClient(
    IOptions<ConsoleOptions> options,
    JsonSerializerOptions jsonOptions)
{
    public async Task<AgentResponse> SendAsync(
        string command,
        object? data,
        CancellationToken cancellationToken)
    {
        JsonElement? element = data is null
            ? null
            : JsonSerializer.SerializeToElement(data, jsonOptions);
        var request = new AgentRequest(
            AgentProtocol.Version,
            command,
            Guid.CreateVersion7().ToString(),
            element);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command switch
        {
            "deploy" => TimeSpan.FromMinutes(10),
            "operate" => TimeSpan.FromMinutes(2),
            "onboard" => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(10)
        });
        await using var pipe = new NamedPipeClientStream(
            ".",
            options.Value.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, jsonOptions);
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(timeout.Token);
        return responseLine is null
            ? throw new IOException("Agent 未返回响应")
            : JsonSerializer.Deserialize<AgentResponse>(responseLine, jsonOptions)
              ?? throw new IOException("Agent 响应无效");
    }
}
