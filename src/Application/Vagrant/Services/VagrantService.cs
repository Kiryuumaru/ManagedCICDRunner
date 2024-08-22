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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private static readonly AbsolutePath TempPath = DataPath / "temp";

    private readonly ExecutorLocker locker = new();

    public async Task<VagrantBuild> Build(string buildId, string rev, Func<AbsolutePath, Task<string>> vagrantfileFactory, CancellationToken cancellationToken)
    {
        string? vagrantFileHash = null;

        AbsolutePath boxPath = BuildPath / buildId;
        AbsolutePath buildFilePath = boxPath / "build.json";
        AbsolutePath packageBoxPath = boxPath / "package.box";
        AbsolutePath vagrantfilePath = boxPath / "Vagrantfile";
        AbsolutePath boxPathTemp = TempPath / $"{buildId}-{Guid.NewGuid()}";
        AbsolutePath vagrantfilePathTemp = boxPathTemp / "Vagrantfile";

        string? currentVagrantFileHash = null;
        string? currentRev = null;
        if (buildFilePath.FileExists() && await buildFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is JsonDocument buildJson &&
            buildJson.RootElement.TryGetProperty("vagrantFileHash", out var vagrantFileHashProp) &&
            buildJson.RootElement.TryGetProperty("rev", out var revProp) &&
            vagrantFileHashProp.ValueKind == JsonValueKind.String &&
            revProp.ValueKind == JsonValueKind.String)
        {
            currentVagrantFileHash = vagrantFileHashProp.GetString()!;
            currentRev = revProp.GetString()!;
        }

        string vagrantfileContent = await vagrantfileFactory(boxPath);

        await vagrantfilePathTemp.WriteAllTextAsync(vagrantfileContent, cancellationToken);
        vagrantFileHash = GetHash(vagrantfilePathTemp);
        await DeleteForce(boxPathTemp, cancellationToken);

        VagrantBuild vagrantBuild = new()
        {
            Id = buildId,
            VagrantFileHash = vagrantFileHash,
            Rev = rev
        };

        if (vagrantFileHash == currentVagrantFileHash && currentRev == rev)
        {
            return vagrantBuild;
        }

        await locker.Execute(buildId, async () => {

            if (boxPath.DirectoryExists())
            {
                await DeleteCore(boxPath, buildId, cancellationToken);
            }

            await vagrantfilePath.WriteAllTextAsync(vagrantfileContent, cancellationToken);

            await Cli.RunListenAndLog(_logger, "vagrant", ["up", "--provider", "hyperv"], boxPath, stoppingToken: cancellationToken);
            await Cli.RunListenAndLog(_logger, "vagrant", ["reload"], boxPath, stoppingToken: cancellationToken);
            await Cli.RunListenAndLog(_logger, "vagrant", ["package", "--output", packageBoxPath], boxPath, stoppingToken: cancellationToken);
            await Cli.RunListenAndLog(_logger, "vagrant", ["box", "add", packageBoxPath, "--name", buildId, "-f"], boxPath, stoppingToken: cancellationToken);

            await DeleteVMCore(boxPath, buildId, cancellationToken);

            var buildObj = new
            {
                id = buildId,
                vagrantFileHash,
                rev
            };
            await buildFilePath.WriteObjAsync(buildObj, cancellationToken: cancellationToken);
        });

        return vagrantBuild;
    }

    public Task<VagrantBuild> Build(string buildId, string rev, string vagrantfile, CancellationToken cancellationToken)
    {
        return Build(buildId, rev, _ => Task.FromResult(vagrantfile), cancellationToken);
    }

    public Task<VagrantBuild> Build(RunnerOSType runnerOSType, string baseBuildId, string buildId, string rev, string inputScript, CancellationToken cancellationToken)
    {
        return Build(buildId, rev, boxPath =>
        {
            string vmGuest;
            string vmCommunicator;
            string vagrantSyncFolder;
            if (runnerOSType == RunnerOSType.Linux)
            {
                vmGuest = ":linux";
                vmCommunicator = "ssh";
                vagrantSyncFolder = "/vagrant";
            }
            else if (runnerOSType == RunnerOSType.Windows)
            {
                vmGuest = ":windows";
                vmCommunicator = "winssh";
                vagrantSyncFolder = "C:/vagrant";
            }
            else
            {
                throw new NotSupportedException();
            }
            return Task.FromResult($"""
                Vagrant.configure("2") do |config|
                  config.vm.box = "{baseBuildId}"
                  config.vm.guest = {vmGuest}
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

        if (!vagrantfilePath.FileExists() || !packageBoxPath.FileExists() || !buildFilePath.FileExists() || await buildFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument buildJson ||
            !buildJson.RootElement.TryGetProperty("id", out var idProp) ||
            !buildJson.RootElement.TryGetProperty("vagrantFileHash", out var vagrantFileHashProp) ||
            !buildJson.RootElement.TryGetProperty("rev", out var revProp) ||
            idProp.ValueKind != JsonValueKind.String ||
            vagrantFileHashProp.ValueKind != JsonValueKind.String ||
            revProp.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string buildId = idProp.GetString()!;
        string buildVagrantFileHash = vagrantFileHashProp.GetString()!;
        string buildRev = revProp.GetString()!;

        return new()
        {
            Id = buildId,
            VagrantFileHash = buildVagrantFileHash,
            Rev = buildRev
        };
    }

    public async Task<Dictionary<string, VagrantBuild?>> GetBuilds(CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, VagrantBuild?> vagrantBuilds = [];
        List<Task> tasks = [];

        foreach (var dir in BuildPath.GetDirectories())
        {
            tasks.Add(Task.Run(async () =>
            {
                var vagrantBuild = await GetBuild(dir.Name, cancellationToken);
                if (vagrantBuild == null)
                {
                    vagrantBuilds[dir.Name] = null;
                }
                else
                {
                    vagrantBuilds[vagrantBuild.Id] = vagrantBuild;
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        return vagrantBuilds.ToDictionary();
    }

    public async Task DeleteBuild(string id, CancellationToken cancellationToken)
    {
        AbsolutePath boxPath = BuildPath / id;
        AbsolutePath buildFilePath = boxPath / "build.json";

        string buildId = id;

        try
        {
            if (await buildFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is JsonDocument buildJson)
            {
                buildId = buildJson.RootElement.GetProperty("id").GetString()!;
            }
        }
        catch { }

        await locker.Execute(buildId, async () =>
        {
            try
            {
                await DeleteCore(boxPath, $"{id}_default_", cancellationToken);
            }
            catch { }
            try
            {
                await Cli.RunOnce("vagrant", ["box", "remove", buildId, "-f"], boxPath, stoppingToken: cancellationToken);
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

        await locker.Execute([buildId, replicaId], async () => {
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
            string vmGuest;
            string vmCommunicator;
            string vagrantSyncFolder;
            if (runnerOSType == RunnerOSType.Linux)
            {
                vmGuest = ":linux";
                vmCommunicator = "ssh";
                vagrantSyncFolder = "/vagrant";
            }
            else if (runnerOSType == RunnerOSType.Windows)
            {
                vmGuest = ":windows";
                vmCommunicator = "winssh";
                vagrantSyncFolder = "C:/vagrant";
            }
            else
            {
                throw new NotSupportedException();
            }

            return Task.FromResult($"""
                Vagrant.configure("2") do |config|
                  config.vm.box = "{buildId}"
                  config.vm.guest = {vmGuest}
                  config.vm.communicator = "{vmCommunicator}"
                  config.vm.synced_folder ".", "{vagrantSyncFolder}", disabled: true
                  config.vm.network "public_network", bridge: "Default Switch"
                  config.vm.provider "hyperv" do |hv|
                    hv.enable_virtualization_extensions = true
                    hv.memory = "{1024 * memoryGB}"
                    hv.cpus = "{cpus}"
                  end
                  config.vm.provision "shell", inline: <<-SHELL
                  SHELL
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
            vmCommunicator = "ssh";
        }
        else
        {
            throw new NotSupportedException();
        }

        await locker.Execute(replicaId, async () =>
        {
            try
            {
                var ct = cancellationToken.WithTimeout(TimeSpan.FromSeconds(30));
                await Task.Run(async () =>
                {
                    await foreach (var cmdEvent in Cli.RunListen("vagrant", [vmCommunicator, "-c", "\"" + await inputScriptFactory() + "\""], replicaPath, stoppingToken: ct))
                    {
                        switch (cmdEvent)
                        {
                            case StandardOutputCommandEvent stdOut:
                                _logger.LogDebug("{x}", stdOut.Text);
                                break;
                            case StandardErrorCommandEvent stdErr:
                                _logger.LogDebug("{x}", stdErr.Text);
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
                }, ct);
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

        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() || await replicaFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument replicaJson ||
            !replicaJson.RootElement.TryGetProperty("buildId", out var buildIdProp) ||
            !replicaJson.RootElement.TryGetProperty("replicaId", out var replicaIdProp) ||
            !replicaJson.RootElement.TryGetProperty("rev", out var revProp) ||
            !replicaJson.RootElement.TryGetProperty("labels", out var labelsProp) ||
            buildIdProp.ValueKind != JsonValueKind.String ||
            replicaIdProp.ValueKind != JsonValueKind.String ||
            revProp.ValueKind != JsonValueKind.String ||
            labelsProp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string buildId = buildIdProp.GetString()!;
        string replicaId = replicaIdProp.GetString()!;
        string replicaRev = revProp.GetString()!;
        var replicaLabels = labelsProp.EnumerateObject().ToDictionary(i => i.Name, i => i.Value.GetString()!)!;

        VagrantReplicaState vagrantReplicaState = VagrantReplicaState.NotCreated;
        await locker.Execute(replicaId, async () =>
        {
            vagrantReplicaState = await GetStateCore(dir, cancellationToken);
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

    public async Task<Dictionary<string, VagrantReplica?>> GetReplicas(CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, VagrantReplica?> vagrantReplicas = [];
        List<Task> tasks = [];

        foreach (var dir in ReplicaPath.GetDirectories())
        {
            tasks.Add(Task.Run(async () =>
            {
                var replica = await GetReplica(dir.Name, cancellationToken);
                if (replica == null)
                {
                    vagrantReplicas[dir.Name] = null;
                }
                else
                {
                    vagrantReplicas[replica.Id] = replica;
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        return vagrantReplicas.ToDictionary();
    }

    public async Task DeleteReplica(string id, CancellationToken cancellationToken)
    {
        AbsolutePath dir = ReplicaPath / id;
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath replicaFilePath = dir / "replica.json";

        await locker.Execute(id, async () =>
        {
            if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists())
            {
                await DeleteForce(dir, cancellationToken);
                return;
            }

            await DeleteCore(dir, $"{id}_default_", cancellationToken);
        });
    }

    public async Task DeleteCore(AbsolutePath dir, string id, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteVMCore(dir, id, cancellationToken);
        }
        catch { }

        await WaitKillable(dir, cancellationToken);

        await DeleteVagrant(dir, cancellationToken);

        await DeleteForce(dir, cancellationToken);
    }

    public async Task DeleteVagrant(AbsolutePath dir, CancellationToken cancellationToken)
    {
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
    }

    public async Task DeleteVMCore(AbsolutePath dir, string id, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var ctxTimed = cancellationToken.WithTimeout(TimeSpan.FromMinutes(2));
                var rawGetVm = await Cli.RunOnce("powershell", ["Get-VM | ConvertTo-Json"], dir, stoppingToken: ctxTimed);
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
                    await Cli.RunOnce("powershell", ["Stop-VM", "-Name", vmId, "-Force"], dir, stoppingToken: ctxTimed);
                    await Cli.RunOnce("powershell", ["Remove-VM", "-Name", vmId, "-Force"], dir, stoppingToken: ctxTimed);
                    while (!ctxTimed.IsCancellationRequested)
                    {
                        var result = await Cli.BuildRun("powershell", ["Get-VM", "-Name", vmId], dir).ExecuteAsync(ctxTimed);
                        if (result.ExitCode != 0)
                        {
                            break;
                        }
                        await Task.Delay(5000, ctxTimed);
                    }
                }
                await DeleteForce(dir / ".vagrant", ctxTimed);
            }
            catch
            {
                _logger.LogWarning("Error on deleting {}. retrying...", id);
            }
        }
    }

    private async Task<VagrantReplicaState> GetStateCore(AbsolutePath vagrantDir, CancellationToken cancellationToken)
    {
        VagrantReplicaState vagrantReplicaState = VagrantReplicaState.NotCreated;
        await foreach (var commandEvent in Cli.RunListen("vagrant", ["status", "--machine-readable"], vagrantDir, stoppingToken: cancellationToken))
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
        return vagrantReplicaState;
    }

    private string GetHash(AbsolutePath filename)
    {
        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(filename);
        var hash = sha512.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private async Task DeleteForce(AbsolutePath path, CancellationToken cancellationToken)
    {
        await WaitKillable(path, cancellationToken);
        await path.DeleteRecursively();
    }

    private async Task WaitKillable(AbsolutePath path, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var processes = await path.GetProcessRecursively();
                if (processes.Count == 0)
                {
                    break;
                }
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.ProcessName.Equals("system", StringComparison.InvariantCultureIgnoreCase) &&
                            !process.ProcessName.Equals("vmms", StringComparison.InvariantCultureIgnoreCase) &&
                            !process.ProcessName.Equals("vmwp", StringComparison.InvariantCultureIgnoreCase))
                        {
                            process.Kill();
                        }
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                await Task.Delay(2000, cancellationToken);
            }
            catch { }
        }
    }
}
