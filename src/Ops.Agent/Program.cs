using CompanyOps.Agent;
using CompanyOps.Agent.Catalog;
using CompanyOps.Agent.Inventory;
using CompanyOps.Agent.Persistence;
using CompanyOps.Agent.Pipe;
using CompanyOps.Agent.Projects;
using CompanyOps.Agent.Operations;
using CompanyOps.Agent.Deployment;
using CompanyOps.Agent.Onboarding;
using CompanyOps.Agent.Updates;
using CompanyOps.Contracts;

if (GitCredentialAskPass.IsRequested())
{
    Environment.ExitCode = GitCredentialAskPass.Run(args);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(
    serviceOptions =>
    {
        serviceOptions.ServiceName = "CompanyOps.Agent";
    });

builder.Services
    .AddOptions<OpsOptions>()
    .Bind(builder.Configuration.GetSection(OpsOptions.SectionName))
    .Validate(
        static options => !string.IsNullOrWhiteSpace(options.PipeName),
        "Ops:PipeName 不能为空")
    .Validate(
        static options => options.InventoryIntervalSeconds is >= 5 and <= 3600,
        "Ops:InventoryIntervalSeconds 必须位于 5 到 3600 秒")
    .Validate(
        OpsOptions.HasSafeAllowedProjectInstallRoots,
        "启用 Ops:EnableMutations 时必须配置非盘符根目录的 Ops:AllowedProjectInstallRoots")
    .ValidateOnStart();

builder.Services.AddSingleton(
    static _ => AgentProtocol.CreateJsonSerializerOptions());

builder.Services.AddSingleton<OpsPathResolver>();
builder.Services.AddSingleton<IManifestCatalog, ManifestCatalog>();
builder.Services.AddSingleton<IOpsStateStore, SqliteOpsStateStore>();
builder.Services.AddSingleton<AgentSnapshotCache>();
builder.Services.AddSingleton<InventoryCoordinator>();
builder.Services.AddSingleton<IProjectRegistry, ProjectRegistry>();
builder.Services.AddSingleton<OperationGate>();
builder.Services.AddSingleton<OperationCoordinator>();
builder.Services.AddSingleton<IOperationSnapshotRefresher, OperationSnapshotRefresher>();
builder.Services.AddSingleton<DeclaredHealthGate>();
builder.Services.AddSingleton<IComponentHealthGate>(
    static provider => provider.GetRequiredService<DeclaredHealthGate>());
builder.Services.AddSingleton<IManifestHealthGate>(
    static provider => provider.GetRequiredService<DeclaredHealthGate>());
builder.Services.AddSingleton<ArtifactPackageValidator>();
builder.Services.AddSingleton<SafeZipExtractor>();
builder.Services.AddSingleton<IPortRegistryStore, SqlitePortRegistryStore>();
builder.Services.AddSingleton<IDeploymentEntrypointAdapter, WindowsServiceDeploymentEntrypointAdapter>();
builder.Services.AddSingleton<IDeploymentActivator, NativeDeploymentActivator>();
builder.Services.AddSingleton<DeploymentEngine>();
builder.Services.AddSingleton<ExistingProjectOnboardingService>();
builder.Services.AddSingleton<ProjectDirectoryBrowser>();
builder.Services.AddSingleton<IGitCredentialStore, GitCredentialStore>();
builder.Services.AddSingleton<IGitCommandRunner, GitCommandRunner>();
builder.Services.AddSingleton<GitUpdateService>();
builder.Services.AddSingleton<FixedCommandRunner>();
builder.Services.AddSingleton<IPm2OwnerControlBridge, NamedPipePm2OwnerControlBridge>();
builder.Services.AddSingleton<IInteractiveSessionControlBridge, NamedPipeInteractiveSessionControlBridge>();
builder.Services.AddSingleton<IComponentControlAdapter, WindowsServiceControlAdapter>();
builder.Services.AddSingleton<IComponentControlAdapter, ScheduledTaskControlAdapter>();
builder.Services.AddSingleton<IComponentControlAdapter>(
    static provider => new IisSiteControlAdapter(provider.GetRequiredService<FixedCommandRunner>(), "iisSite"));
builder.Services.AddSingleton<IComponentControlAdapter>(
    static provider => new IisSiteControlAdapter(provider.GetRequiredService<FixedCommandRunner>(), "staticSite"));
builder.Services.AddSingleton<IComponentControlAdapter, Pm2LegacyControlAdapter>();
builder.Services.AddSingleton<IComponentControlAdapter, InteractiveAppControlAdapter>();
builder.Services.AddSingleton<IInventorySource, WindowsServiceInventorySource>();
builder.Services.AddSingleton<IInventorySource, NetworkPortInventorySource>();
builder.Services.AddSingleton<IInventorySource, IisInventorySource>();
builder.Services.AddSingleton<IInventorySource, ScheduledTaskInventorySource>();
builder.Services.AddSingleton<IInventorySource, Pm2InventorySource>();
builder.Services.AddSingleton<IInventorySource, InteractiveAppInventorySource>();
builder.Services.AddSingleton<ILegacyPm2ClaimProvider, LegacyPm2ClaimProvider>();
builder.Services.AddSingleton<Pm2SnapshotReader>();
builder.Services.AddSingleton<IInteractiveSessionClaimProvider, InteractiveSessionClaimProvider>();
builder.Services.AddSingleton<InteractiveSnapshotReader>();
builder.Services.AddSingleton<InteractiveEntrypointStateStore>();
builder.Services.AddSingleton<IDeploymentEntrypointAdapter, InteractiveAppDeploymentEntrypointAdapter>();
builder.Services.AddSingleton<NamedPipeSecurityFactory>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<NamedPipeServer>();

await builder.Build().RunAsync();
