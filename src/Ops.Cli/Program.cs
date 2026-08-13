using System.IO.Pipes;
using System.Text.Json;
using CompanyOps.Contracts;

var command = ReadCommand(args) ?? "inventory";
var pipeName = ReadOption(args, "--pipe")
               ?? Environment.GetEnvironmentVariable("COMPANYOPS_PIPE_NAME")
               ?? "CompanyOps.Agent.v1";
var defaultTimeout = command switch
{
    "deploy" => TimeSpan.FromMinutes(10),
    "operate" => TimeSpan.FromMinutes(2),
    _ => TimeSpan.FromSeconds(10)
};
var timeoutText = ReadOption(args, "--timeout-seconds");
if (timeoutText is not null &&
    (!int.TryParse(timeoutText, out var configuredTimeoutSeconds) || configuredTimeoutSeconds is < 1 or > 1800))
{
    Console.Error.WriteLine("--timeout-seconds 必须是 1 到 1800 之间的整数。");
    return 2;
}

var commandTimeout = timeoutText is null
    ? defaultTimeout
    : TimeSpan.FromSeconds(int.Parse(timeoutText, System.Globalization.CultureInfo.InvariantCulture));

var transportJsonOptions = AgentProtocol.CreateJsonSerializerOptions();
var displayJsonOptions = AgentProtocol.CreateJsonSerializerOptions(writeIndented: true);
JsonElement? requestData = null;
var dataText = ReadOption(args, "--data");
var dataFile = ReadOption(args, "--data-file");
if (dataText is not null && dataFile is not null)
{
    Console.Error.WriteLine("--data 与 --data-file 不能同时使用。");
    return 2;
}

try
{
    if (dataFile is not null)
    {
        requestData = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(dataFile));
    }
    else if (dataText is not null)
    {
        requestData = JsonSerializer.Deserialize<JsonElement>(dataText);
    }
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"请求 data 读取失败：{exception.Message}");
    return 2;
}

var request = new AgentRequest(
    AgentProtocol.Version,
    command,
    Guid.CreateVersion7().ToString(),
    requestData);

using var pipe = new NamedPipeClientStream(
    ".",
    pipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous,
    System.Security.Principal.TokenImpersonationLevel.Identification);
using var timeout = new CancellationTokenSource(commandTimeout);

try
{
    await pipe.ConnectAsync(timeout.Token);
    var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, transportJsonOptions);
    await pipe.WriteAsync(requestBytes, timeout.Token);
    await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
    await pipe.FlushAsync(timeout.Token);

    using var reader = new StreamReader(
        pipe,
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 4096,
        leaveOpen: true);
    var responseLine = await reader.ReadLineAsync(timeout.Token);
    if (responseLine is null)
    {
        Console.Error.WriteLine("Agent 未返回响应。");
        return 2;
    }

    using var response = JsonDocument.Parse(responseLine);
    Console.WriteLine(JsonSerializer.Serialize(response.RootElement, displayJsonOptions));
    return response.RootElement.GetProperty("success").GetBoolean() ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(
        $"命令 {command} 在 {commandTimeout.TotalSeconds:0} 秒内未完成；先查询 audit 和目标状态，不要盲目更换幂等键重试。");
    return 2;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"Named Pipe 通信失败：{exception.Message}");
    return 2;
}

static string? ReadOption(string[] arguments, string optionName)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], optionName, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static string? ReadCommand(string[] arguments)
{
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            index++;
            continue;
        }

        return arguments[index];
    }

    return null;
}
