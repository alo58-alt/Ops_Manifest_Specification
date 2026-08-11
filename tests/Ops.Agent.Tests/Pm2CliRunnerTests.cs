using CompanyOps.Contracts;
using CompanyOps.Pm2Bridge;
using Microsoft.Extensions.Options;

namespace CompanyOps.Agent.Tests;

public sealed class Pm2CliRunnerTests
{
    [Fact]
    public async Task ExactNamePmIdCwdAndScript_ControlsOnlyNumericPmId()
    {
        using var directory = new TestDirectory();
        var actionLog = Path.Combine(directory.FullPath, "action.log");
        var runner = CreateRunner(directory.FullPath, actionLog);
        var request = new Pm2BridgeControlRequest(
            Pm2BridgeProtocol.Version,
            "request-1",
            7,
            "sample-worker",
            "C:\\Apps\\sample",
            "C:\\Apps\\sample\\worker.js",
            ComponentOperationAction.Restart);

        var result = await runner.ControlAsync(request, CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.Equal("restart 7", File.ReadAllText(actionLog));
    }

    [Fact]
    public async Task CwdMismatch_FailsBeforeControlCommand()
    {
        using var directory = new TestDirectory();
        var actionLog = Path.Combine(directory.FullPath, "action.log");
        var runner = CreateRunner(directory.FullPath, actionLog);
        var request = new Pm2BridgeControlRequest(
            Pm2BridgeProtocol.Version,
            "request-2",
            7,
            "sample-worker",
            "C:\\Apps\\other",
            "C:\\Apps\\sample\\worker.js",
            ComponentOperationAction.Stop);

        var result = await runner.ControlAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(actionLog));
    }

    private static Pm2CliRunner CreateRunner(string directory, string actionLog)
    {
        var script = Path.Combine(directory, "fake-pm2.mjs");
        File.WriteAllText(
            script,
            $$"""
            import fs from 'node:fs';
            const action = process.argv[2];
            if (action === 'jlist') {
              console.log(JSON.stringify([{
                name: 'sample-worker', pm_id: 7, pid: 1234,
                pm2_env: {
                  pm_cwd: 'C:\\Apps\\sample',
                  pm_exec_path: 'C:\\Apps\\sample\\worker.js',
                  status: 'online', restart_time: 2
                }
              }]));
            } else {
              fs.writeFileSync({{System.Text.Json.JsonSerializer.Serialize(actionLog)}}, `${action} ${process.argv[3]}`);
            }
            """);
        return new Pm2CliRunner(Options.Create(new BridgeOptions
        {
            NodeExecutablePath = FindNode(),
            Pm2CliPath = script,
            ManifestDirectory = directory,
            SnapshotDirectory = directory
        }));
    }

    private static string FindNode()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "node.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("测试环境缺少 node.exe");
    }
}
