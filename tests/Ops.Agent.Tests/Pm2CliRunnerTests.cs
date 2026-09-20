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

    [Fact]
    public async Task Register_UsesStructuredArgumentsAndReturnsFreshPmId()
    {
        using var directory = new TestDirectory();
        var actionLog = Path.Combine(directory.FullPath, "action.log");
        var runner = CreateMutationRunner(directory.FullPath, actionLog, initiallyPresent: false);
        var request = new Pm2BridgeMutationRequest(
            Pm2BridgeProtocol.MutationVersion,
            "register-1",
            Pm2BridgeMutationAction.Register,
            Registration: new Pm2BridgeRegistration(
                "sample-worker", "C:\\Apps\\sample-v2", "C:\\Apps\\sample-v2\\worker.js", ["--port", "5010"]));

        var result = await runner.MutateAsync(request, CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(9, result.Process?.PmId);
        var invoked = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(actionLog)) ?? [];
        Assert.Equal(["start", "C:\\Apps\\sample-v2\\worker.js", "--name", "sample-worker", "--cwd", "C:\\Apps\\sample-v2", "--", "--port", "5010"], invoked);
        Assert.DoesNotContain(invoked, value => value is "all" or "kill");
    }

    [Fact]
    public async Task Delete_RequiresArgumentsAndDeletesOnlyNumericPmId()
    {
        using var directory = new TestDirectory();
        var actionLog = Path.Combine(directory.FullPath, "action.log");
        var runner = CreateMutationRunner(directory.FullPath, actionLog, initiallyPresent: true);
        var mismatch = new Pm2BridgeMutationRequest(
            Pm2BridgeProtocol.MutationVersion, "delete-mismatch", Pm2BridgeMutationAction.Delete,
            new Pm2BridgeProcessIdentity(7, "sample-worker", "C:\\Apps\\sample", "C:\\Apps\\sample\\worker.js", ["wrong"]));

        var rejected = await runner.MutateAsync(mismatch, CancellationToken.None);

        Assert.False(rejected.Success);
        Assert.False(File.Exists(actionLog));

        var exact = mismatch with
        {
            RequestId = "delete-exact",
            ExpectedProcess = mismatch.ExpectedProcess! with { Arguments = ["--port", "5000"] }
        };
        var deleted = await runner.MutateAsync(exact, CancellationToken.None);

        Assert.True(deleted.Success, deleted.Detail);
        var invoked = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(actionLog)) ?? [];
        Assert.Equal(["delete", "7"], invoked);
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

    private static Pm2CliRunner CreateMutationRunner(string directory, string actionLog, bool initiallyPresent)
    {
        var script = Path.Combine(directory, "fake-pm2-mutation.mjs");
        var state = Path.Combine(directory, "state.json");
        File.WriteAllText(state, initiallyPresent ? "true" : "false");
        File.WriteAllText(
            script,
            $$"""
            import fs from 'node:fs';
            const statePath = {{System.Text.Json.JsonSerializer.Serialize(state)}};
            const logPath = {{System.Text.Json.JsonSerializer.Serialize(actionLog)}};
            const action = process.argv[2];
            const present = JSON.parse(fs.readFileSync(statePath, 'utf8'));
            const emit = (v, id, cwd, script, args) => console.log(JSON.stringify(v ? [{
              name: 'sample-worker', pm_id: id, pid: 1234,
              pm2_env: { pm_cwd: cwd, pm_exec_path: script, status: 'online', restart_time: 0, args }
            }] : []));
            if (action === 'jlist') {
              emit(present, present && fs.existsSync(logPath) ? 9 : 7,
                present && fs.existsSync(logPath) ? 'C:\\Apps\\sample-v2' : 'C:\\Apps\\sample',
                present && fs.existsSync(logPath) ? 'C:\\Apps\\sample-v2\\worker.js' : 'C:\\Apps\\sample\\worker.js',
                present && fs.existsSync(logPath) ? ['--port', '5010'] : ['--port', '5000']);
            } else {
              fs.writeFileSync(logPath, JSON.stringify(process.argv.slice(2)));
              fs.writeFileSync(statePath, action === 'start' ? 'true' : 'false');
            }
            """);
        return new Pm2CliRunner(Options.Create(new BridgeOptions
        {
            NodeExecutablePath = FindNode(), Pm2CliPath = script,
            ManifestDirectory = directory, SnapshotDirectory = directory
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
