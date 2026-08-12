using CompanyOps.Agent.Deployment;

namespace CompanyOps.Agent.Tests;

internal sealed class PassthroughDeploymentActivator : IDeploymentActivator
{
    public Task<DeploymentActivationResult> PlanAsync(
        DeploymentActivationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DeploymentActivationResult(true, "测试激活计划已就绪"));
    }

    public Task<DeploymentActivationResult> ActivateAsync(
        DeploymentActivationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            Directory.Exists(request.ReleasePath)
                ? new DeploymentActivationResult(
                    true,
                    "测试 release 已就绪",
                    Rollback: new PassthroughRollback())
                : new DeploymentActivationResult(false, "测试 release 目录不存在"));
    }

    private sealed class PassthroughRollback : IDeploymentActivationRollback
    {
        public Task<DeploymentActivationResult> RestoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DeploymentActivationResult(true, "测试激活状态已恢复"));
        }
    }
}
