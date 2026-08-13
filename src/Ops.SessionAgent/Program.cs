using CompanyOps.Contracts;
using CompanyOps.SessionAgent;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<SessionAgentOptions>()
    .Bind(builder.Configuration.GetSection(SessionAgentOptions.SectionName))
    .Validate(static options => Path.IsPathFullyQualified(options.ManifestDirectory), "ManifestDirectory 必须是绝对路径")
    .Validate(static options => Path.IsPathFullyQualified(options.SnapshotDirectory), "SnapshotDirectory 必须是绝对路径")
    .Validate(static options => !string.IsNullOrWhiteSpace(options.PipeName), "PipeName 不能为空")
    .Validate(static options => options.SnapshotIntervalSeconds is >= 5 and <= 300, "SnapshotIntervalSeconds 超出范围")
    .ValidateOnStart();
builder.Services.AddSingleton(static _ => AgentProtocol.CreateJsonSerializerOptions());
builder.Services.AddSingleton<InteractiveClaimReader>();
builder.Services.AddSingleton<InteractiveProcessManager>();
builder.Services.AddHostedService<InteractiveStartupWorker>();
builder.Services.AddHostedService<InteractiveSnapshotWorker>();
builder.Services.AddHostedService<InteractiveControlServer>();
await builder.Build().RunAsync();
