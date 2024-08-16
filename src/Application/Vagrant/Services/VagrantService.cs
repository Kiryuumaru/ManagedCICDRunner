using Application.Common;
using Application.Runner.Services;
using CliWrap.EventStream;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Runner.Models;
using Domain.Vagrant.Enums;
using Domain.Vagrant.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.Vagrant.Services;

public class VagrantService(ILogger<VagrantService> logger)
{
    private readonly ILogger<VagrantService> _logger = logger;

    private static readonly AbsolutePath DataPath = Defaults.DataPath / "vagrant";
    private static readonly AbsolutePath BuildPath = DataPath / "build";
    private static readonly AbsolutePath ReplicaPath = DataPath / "replica";

    private readonly ExecutorLocker locker = new();

    public async Task Build(string buildId, string rev, Func<AbsolutePath, Task<string>> vagrantfileFactory, CancellationToken cancellationToken)
    {
        AbsolutePath boxPath = BuildPath / buildId;
        AbsolutePath buildFilePath = boxPath / "build.json";

        string? currentRev = null;
        if (buildFilePath.FileExists() && await buildFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is JsonDocument buildJson)
        {
            currentRev = buildJson.RootElement.GetProperty("rev").GetString()!;
        }

        if (currentRev == rev)
        {
            return;
        }

        AbsolutePath packageBoxPath = boxPath / "package.box";
        AbsolutePath vagrantfilePath = boxPath / "Vagrantfile";

        await packageBoxPath.DeleteRecursively();
        await vagrantfilePath.DeleteRecursively();

        boxPath.CreateDirectory();

        await vagrantfilePath.WriteAllTextAsync(await vagrantfileFactory(boxPath), cancellationToken);

        await locker.Execute(buildId, async () => {
            await Cli.RunListenAndLog(_logger, "vagrant", ["up", "--provider", "hyperv"], boxPath, stoppingToken: cancellationToken);
            await Cli.RunListenAndLog(_logger, "vagrant", ["reload"], boxPath, stoppingToken: cancellationToken);
            await Cli.RunListenAndLog(_logger, "vagrant", ["package", "--output", packageBoxPath], boxPath, stoppingToken: cancellationToken);
            await Cli.RunListenAndLog(_logger, "vagrant", ["box", "add", packageBoxPath, "--name", buildId, "-f"], boxPath, stoppingToken: cancellationToken);
        });

        var buildObj = new
        {
            id = buildId,
            rev
        };
        await buildFilePath.WriteObjAsync(buildObj, cancellationToken: cancellationToken);
    }

    public Task Build(string buildId, string rev, string vagrantfile, CancellationToken cancellationToken)
    {
        return Build(buildId, rev, _ => Task.FromResult(vagrantfile), cancellationToken);
    }

    public Task Build(RunnerOSType runnerOSType, string baseBuildId, string buildId, string rev, string inputScript, CancellationToken cancellationToken)
    {
        return Build(buildId, rev, boxPath =>
        {
            string vmCommunicator;
            string vagrantSyncFolder;
            if (runnerOSType == RunnerOSType.Linux)
            {
                vmCommunicator = "ssh";
                vagrantSyncFolder = "/vagrant";
            }
            else if (runnerOSType == RunnerOSType.Windows)
            {
                vmCommunicator = "winrm";
                vagrantSyncFolder = "C:/vagrant";
            }
            else
            {
                throw new NotSupportedException();
            }
            return Task.FromResult($"""
                Vagrant.configure("2") do |config|
                  config.vm.box = "{baseBuildId}"
                  config.vm.communicator = "{vmCommunicator}"
                  config.vm.synced_folder ".", "{vagrantSyncFolder}", disabled: true
                  config.vm.network "public_network", bridge: "Default Switch"
                  config.vm.provider "hyperv" do |hv|
                    hv.enable_virtualization_extensions = true
                  end
                  config.vm.provision "shell", inline: <<-SHELL
                    {inputScript}
                  SHELL
                end
                """);
        }, cancellationToken);
    }

    public async Task<VagrantBuild?> GetBuild(string id, CancellationToken cancellationToken)
    {
        AbsolutePath dir = BuildPath / id;
        AbsolutePath packageBoxPath = dir / "package.box";
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath buildFilePath = dir / "build.json";

        if (!vagrantfilePath.FileExists() || !packageBoxPath.FileExists() || !buildFilePath.FileExists() || await buildFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument buildJson)
        {
            return null;
        }

        string buildId = buildJson.RootElement.GetProperty("id").GetString()!;
        string buildRev = buildJson.RootElement.GetProperty("rev").GetString()!;

        return new()
        {
            Id = buildId,
            Rev = buildRev
        };
    }

    public async Task<VagrantBuild[]> GetBuilds(CancellationToken cancellationToken)
    {
        List<VagrantBuild> vagrantBuilds = [];

        foreach (var dir in BuildPath.GetDirectories())
        {
            var build = await GetBuild(dir.Name, cancellationToken);

            if (build != null)
            {
                vagrantBuilds.Add(build);
            }
        }

        return [.. vagrantBuilds];
    }

    public async Task DeleteBuild(string id, CancellationToken cancellationToken)
    {
        AbsolutePath boxPath = BuildPath / id;
        AbsolutePath buildFilePath = boxPath / "build.json";

        if (await buildFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument buildJson)
        {
            return;
        }

        string buildId = buildJson.RootElement.GetProperty("id").GetString()!;

        await locker.Execute(buildId, async () => {
            try
            {
                await DeleteCore(boxPath, $"{id}_default_", cancellationToken);
            }
            catch { }
            try
            {
                await Cli.RunListenAndLog(_logger, "vagrant", ["box", "remove", buildId, "-f"], boxPath, stoppingToken: cancellationToken);
            }
            catch { }
            try
            {
                await boxPath.DeleteRecursively();
            }
            catch { }
        });
    }

    public async Task Run(string buildId, string replicaId, string rev, Func<AbsolutePath, Task<string>> vagrantfileFactory, Dictionary<string, string> labels, CancellationToken cancellationToken)
    {
        AbsolutePath replicaPath = ReplicaPath / replicaId;
        AbsolutePath vagrantfilePath = replicaPath / "Vagrantfile";
        AbsolutePath replicaFilePath = replicaPath / "replica.json";

        if (replicaPath.DirectoryExists() || replicaPath.FileExists())
        {
            throw new Exception($"Error running vagrant replica \"{replicaId}\": Replica already exists");
        }

        await locker.Execute(replicaId, async () => {
            var replicaObj = new
            {
                buildId,
                replicaId,
                rev,
                labels
            };

            await replicaFilePath.WriteObjAsync(replicaObj, cancellationToken: cancellationToken);
            await vagrantfilePath.WriteAllTextAsync(await vagrantfileFactory(replicaPath), cancellationToken: cancellationToken);

            await foreach (var cmdEvent in Cli.RunListen("vagrant", ["up", "--provider", "hyperv"], replicaPath, stoppingToken: cancellationToken))
            {
                switch (cmdEvent)
                {
                    case StandardOutputCommandEvent stdOut:
                        _logger.LogDebug("{x}", stdOut.Text);
                        break;
                    case StandardErrorCommandEvent stdErr:
                        _logger.LogError("{x}", stdErr.Text);
                        break;
                    case ExitedCommandEvent exited:
                        var msg = $"vagrant up ended with return code {exited.ExitCode}";
                        if (exited.ExitCode != 0)
                        {
                            throw new Exception(msg);
                        }
                        else
                        {
                            _logger.LogDebug("{x}", msg);
                        }
                        break;
                }
            }
        });
    }

    public Task Run(string buildId, string replicaId, string rev, string vagrantfile, Dictionary<string, string> labels, CancellationToken cancellationToken)
    {
        return Run(buildId, replicaId, rev, _ => Task.FromResult(vagrantfile), labels, cancellationToken);
    }

    public Task Run(RunnerOSType runnerOSType, string buildId, string replicaId, string rev, int cpus, int memoryGB, Dictionary<string, string> labels, CancellationToken cancellationToken)
    {
        return Run(buildId, replicaId, rev, replicaPath =>
        {
            string vmCommunicator;
            string vagrantSyncFolder;

            if (runnerOSType == RunnerOSType.Linux)
            {
                vmCommunicator = "ssh";
                vagrantSyncFolder = "/vagrant";
            }
            else if (runnerOSType == RunnerOSType.Windows)
            {
                vmCommunicator = "winrm";
                vagrantSyncFolder = "C:/vagrant";
            }
            else
            {
                throw new NotSupportedException();
            }

            return Task.FromResult($"""
                Vagrant.configure("2") do |config|
                  config.vm.box = "{buildId}"
                  config.vm.communicator = "{vmCommunicator}"
                  config.vm.synced_folder ".", "{vagrantSyncFolder}", disabled: true
                  config.vm.network "public_network", bridge: "Default Switch"
                  config.vm.provider "hyperv" do |hv|
                    hv.enable_virtualization_extensions = true
                    hv.memory = "{1024 * memoryGB}"
                    hv.cpus = "{cpus}"
                  end
                end
                """);
        }, labels, cancellationToken);
    }

    public async Task Execute(RunnerOSType runnerOSType, string replicaId, Func<Task<string>> inputScriptFactory, CancellationToken cancellationToken)
    {
        AbsolutePath replicaPath = ReplicaPath / replicaId;
        AbsolutePath vagrantfilePath = replicaPath / "Vagrantfile";
        AbsolutePath replicaFilePath = replicaPath / "replica.json";

        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists())
        {
            throw new Exception($"Error executing script from vagrant replica \"{replicaId}\": Replica does not exists");
        }

        string vmCommunicator;
        if (runnerOSType == RunnerOSType.Linux)
        {
            vmCommunicator = "ssh";
        }
        else if (runnerOSType == RunnerOSType.Windows)
        {
            vmCommunicator = "winrm";
        }
        else
        {
            throw new NotSupportedException();
        }

        await locker.Execute(replicaId, async () =>
        {
            try
            {
                CancellationTokenSource ctsTs = new(TimeSpan.FromSeconds(30));
                await Task.Run(async () =>
                {
                    await foreach (var cmdEvent in Cli.RunListen("vagrant", [vmCommunicator, "-c", "\"" + await inputScriptFactory() + "\""], replicaPath, stoppingToken: cancellationToken))
                    {
                        switch (cmdEvent)
                        {
                            case StandardOutputCommandEvent stdOut:
                                _logger.LogDebug("{x}", stdOut.Text);
                                break;
                            case StandardErrorCommandEvent stdErr:
                                _logger.LogError("{x}", stdErr.Text);
                                break;
                            case ExitedCommandEvent exited:
                                var msg = $"vagrant {vmCommunicator} ended with return code {exited.ExitCode}";
                                if (exited.ExitCode != 0)
                                {
                                    throw new Exception(msg);
                                }
                                else
                                {
                                    _logger.LogDebug("{x}", msg);
                                }
                                break;
                        }
                    }
                }, ctsTs.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                throw;
            }
        });
    }

    public async Task<VagrantReplica?> GetReplica(string id, CancellationToken cancellationToken)
    {
        AbsolutePath dir = ReplicaPath / id;
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath replicaFilePath = dir / "replica.json";
        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() || await replicaFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument replicaJson)
        {
            return null;
        }

        string buildId = replicaJson.RootElement.GetProperty("buildId").GetString()!;
        string replicaId = replicaJson.RootElement.GetProperty("replicaId").GetString()!;
        string replicaRev = replicaJson.RootElement.GetProperty("rev").GetString()!;
        var replicaLabels = replicaJson.RootElement.GetProperty("labels").EnumerateObject().ToDictionary(i => i.Name, i => i.Value.GetString()!)!;

        VagrantReplicaState vagrantReplicaState = VagrantReplicaState.NotCreated;
        await locker.Execute(replicaId, async () =>
        {
            await foreach (var commandEvent in Cli.RunListen("vagrant", ["status", "--machine-readable"], dir, stoppingToken: cancellationToken))
            {
                string line = "";
                switch (commandEvent)
                {
                    case StandardOutputCommandEvent outEvent:
                        _logger.LogDebug("{x}", outEvent.Text);
                        line = outEvent.Text;
                        break;
                    case StandardErrorCommandEvent errEvent:
                        _logger.LogDebug("{x}", errEvent.Text);
                        line = errEvent.Text;
                        break;
                }
                if (!string.IsNullOrEmpty(line))
                {
                    string[] split = line.Split(',');
                    if (split.Length > 3 && split[2].Equals("state", StringComparison.InvariantCultureIgnoreCase))
                    {
                        vagrantReplicaState = split[3].ToLowerInvariant() switch
                        {
                            "not_created" => VagrantReplicaState.NotCreated,
                            "off" => VagrantReplicaState.Off,
                            "poweroff" => VagrantReplicaState.Off,
                            "stopping" => VagrantReplicaState.Off,
                            "running" => VagrantReplicaState.Running,
                            "starting" => VagrantReplicaState.Starting,
                            _ => throw new NotImplementedException($"{split[3]} is not implemented as VagrantReplicaState")
                        };
                    }
                }
            }
        });

        return new()
        {
            BuildId = buildId,
            Id = replicaId,
            Rev = replicaRev,
            State = vagrantReplicaState,
            Labels = replicaLabels
        };
    }

    public async Task<VagrantReplica[]> GetReplicas(CancellationToken cancellationToken)
    {
        List<VagrantReplica> vagrantReplicas = [];

        foreach (var dir in ReplicaPath.GetDirectories())
        {
            var replica = await GetReplica(dir.Name, cancellationToken);

            if (replica != null)
            {
                vagrantReplicas.Add(replica);
            }
        }

        return [.. vagrantReplicas];
    }

    public async Task DeleteReplica(string id, CancellationToken cancellationToken)
    {
        AbsolutePath dir = ReplicaPath / id;
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath replicaFilePath = dir / "replica.json";

        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists())
        {
            return;
        }

        await locker.Execute(id, async () =>
        {
            await DeleteCore(dir, $"{id}_default_", cancellationToken);
        });
    }

    public async Task DeleteCore(AbsolutePath dir, string id, CancellationToken cancellationToken)
    {
        try
        {
            var rawGetVm = await Cli.RunOnce("powershell", ["Get-VM | ConvertTo-Json"], dir, stoppingToken: cancellationToken);
            var getVmJson = JsonSerializer.Deserialize<JsonDocument>(rawGetVm)!;
            string? vmId = null;
            foreach (var prop in getVmJson.RootElement.EnumerateArray())
            {
                var vmName = prop!.GetProperty("Name").GetString()!;
                if (prop!.GetProperty("Name").GetString()!.StartsWith(id, StringComparison.InvariantCultureIgnoreCase))
                {
                    vmId = vmName;
                }
            }
            if (vmId != null)
            {
                await Cli.RunOnce("powershell", ["Stop-VM", "-Name", vmId, "-Force"], dir, stoppingToken: cancellationToken);
                await Cli.RunOnce("powershell", ["Remove-VM", "-Name", vmId, "-Force"], dir, stoppingToken: cancellationToken);
            }

            (dir / ".vagrant").DeleteDirectory();
        }
        catch { }

        await foreach (var cmdEvent in Cli.RunListen("vagrant", ["destroy", "-f"], dir, stoppingToken: cancellationToken))
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    _logger.LogDebug("{x}", stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    _logger.LogError("{x}", stdErr.Text);
                    break;
                case ExitedCommandEvent exited:
                    var msg = $"vagrant destroy -f ended with return code {exited.ExitCode}";
                    if (exited.ExitCode != 0)
                    {
                        throw new Exception(msg);
                    }
                    else
                    {
                        _logger.LogDebug("{x}", msg);
                    }
                    break;
            }
        }

        dir.DeleteDirectory();
    }
}
