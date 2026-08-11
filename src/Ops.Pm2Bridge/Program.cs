using CompanyOps.Contracts;
using CompanyOps.Pm2Bridge;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<BridgeOptions>()
    .Bind(builder.Configuration.GetSection(BridgeOptions.SectionName))
    .Validate(static options => !string.IsNullOrWhiteSpace(options.PipeName), "PipeName 不能为空")
    .Validate(static options => Path.IsPathFullyQualified(options.ManifestDirectory), "ManifestDirectory 必须是绝对路径")
    .Validate(static options => Path.IsPathFullyQualified(options.SnapshotDirectory), "SnapshotDirectory 必须是绝对路径")
    .Validate(static options => Path.IsPathFullyQualified(options.NodeExecutablePath) && File.Exists(options.NodeExecutablePath), "NodeExecutablePath 必须存在")
    .Validate(static options => Path.IsPathFullyQualified(options.Pm2CliPath) && File.Exists(options.Pm2CliPath), "Pm2CliPath 必须存在")
    .Validate(static options => options.SnapshotIntervalSeconds is >= 5 and <= 300, "SnapshotIntervalSeconds 超出范围")
    .ValidateOnStart();
builder.Services.AddSingleton(static _ => AgentProtocol.CreateJsonSerializerOptions());
builder.Services.AddSingleton<Pm2CliRunner>();
builder.Services.AddHostedService<SnapshotWorker>();
builder.Services.AddHostedService<ControlServer>();
await builder.Build().RunAsync();
