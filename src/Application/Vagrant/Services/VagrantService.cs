using Application.Common;
using Application.Runner.Services;
using CliWrap.EventStream;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Runner.Models;
using Domain.Vagrant.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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

    public async Task Build(string buildId, string rev, Func<AbsolutePath, Task<string>> vagrantfileFactory, AbsolutePath[] hostAssets, CancellationToken cancellationToken)
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

        boxPath.CreateDirectory();

        await vagrantfilePath.WriteAllTextAsync(await vagrantfileFactory(boxPath), cancellationToken);

        foreach (var hostAsset in hostAssets)
        {
            if (hostAsset.FileExists())
            {
                await hostAsset.CopyRecursively(boxPath / hostAsset.Name);
            }
            else
            {
                await hostAsset.CopyRecursively(boxPath / "assets");
            }
        }

        await Cli.RunListenAndLog(_logger, $"vagrant up", boxPath, stoppingToken: cancellationToken);
        await Cli.RunListenAndLog(_logger, $"vagrant package --output \"{packageBoxPath}\"", boxPath, stoppingToken: cancellationToken);
        await Cli.RunListenAndLog(_logger, $"vagrant box add {packageBoxPath} --name {buildId} -f", boxPath, stoppingToken: cancellationToken);
        await Cli.RunListenAndLog(_logger, $"vagrant destroy -f", boxPath, stoppingToken: cancellationToken);

        var buildObj = new
        {
            buildId,
            rev
        };
        await buildFilePath.WriteObjAsync(buildObj, cancellationToken: cancellationToken);
    }

    public Task Build(string buildId, string rev, string vagrantfile, AbsolutePath[] hostAssets, CancellationToken cancellationToken)
    {
        return Build(buildId, rev, _ => Task.FromResult(vagrantfile), hostAssets, cancellationToken);
    }

    public Task Build(RunnerOSType runnerOSType, string baseBuildId, string buildId, string rev, string inputScript, AbsolutePath[] hostAssets, CancellationToken cancellationToken)
    {
        return Build(buildId, rev, async boxPath =>
        {
            AbsolutePath bootstrapPath;
            string vmCommunicator;
            if (runnerOSType == RunnerOSType.Linux)
            {
                bootstrapPath = boxPath / "bootstrap.sh";
                vmCommunicator = "ssh";
            }
            else if (runnerOSType == RunnerOSType.Windows)
            {
                bootstrapPath = boxPath / "bootstrap.ps1";
                vmCommunicator = "winrm";
            }
            else
            {
                throw new NotSupportedException();
            }
            await bootstrapPath.WriteAllTextAsync(inputScript, cancellationToken);
            return $"""
                Vagrant.configure("2") do |config|
                  config.vm.box = "{baseBuildId}"
                  config.vm.provision "shell", path: "{bootstrapPath.Name}"
                  config.vm.communicator = "{vmCommunicator}"
                end
                """;
        }, hostAssets, cancellationToken);
    }

    public async Task<VagrantBuild[]> GetBuilds(CancellationToken cancellationToken)
    {
        List<VagrantBuild> vagrantBuilds = [];

        foreach (var dir in BuildPath.GetDirectories())
        {
            AbsolutePath packageBoxPath = dir / "package.box";
            AbsolutePath vagrantfilePath = dir / "Vagrantfile";
            AbsolutePath buildFilePath = dir / "build.json";

            if (!vagrantfilePath.FileExists() || !packageBoxPath.FileExists() || !buildFilePath.FileExists() || await buildFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument buildJson)
            {
                continue;
            }

            string buildId = buildJson.RootElement.GetProperty("id").GetString()!;
            string buildRev = buildJson.RootElement.GetProperty("rev").GetString()!;

            vagrantBuilds.Add(new()
            {
                Id = buildId,
                Rev = buildRev
            });
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

        try
        {
            await Cli.RunListenAndLog(_logger, $"vagrant destroy -f", boxPath, stoppingToken: cancellationToken);
        }
        catch { }
        try
        {
            await Cli.RunListenAndLog(_logger, $"vagrant box remove {buildId} -f", boxPath, stoppingToken: cancellationToken);
        }
        catch { }
        try
        {
            await boxPath.DeleteRecursively();
        }
        catch { }
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

        var replicaObj = new
        {
            buildId,
            replicaId,
            rev,
            labels
        };
        await replicaFilePath.WriteObjAsync(replicaObj, cancellationToken: cancellationToken);

        await vagrantfilePath.WriteAllTextAsync(await vagrantfileFactory(replicaPath), cancellationToken: cancellationToken);

        await Cli.RunListenAndLog(_logger, $"vagrant up", replicaPath, stoppingToken: cancellationToken);
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

        await Cli.RunListenAndLog(_logger, $"vagrant {vmCommunicator} -c \"{await inputScriptFactory()}\"", replicaPath, stoppingToken: cancellationToken);
    }

    public async Task<VagrantReplica[]> GetReplicas(CancellationToken cancellationToken)
    {
        List<VagrantReplica> vagrantReplicas = [];

        foreach (var dir in ReplicaPath.GetDirectories())
        {
            AbsolutePath vagrantfilePath = dir / "Vagrantfile";
            AbsolutePath replicaFilePath = dir / "replica.json";
            if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() || await replicaFilePath.ReadObjAsync<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument replicaJson)
            {
                continue;
            }

            string replicaId = replicaJson.RootElement.GetProperty("replicaId").GetString()!;
            string replicaRev = replicaJson.RootElement.GetProperty("rev").GetString()!;
            var replicaLabels = replicaJson.RootElement.GetProperty("labels").EnumerateObject().ToDictionary(i => i.Name, i => i.Value.GetString()!)!;

            vagrantReplicas.Add(new()
            {
                Id = replicaId,
                Rev = replicaRev,
                State = Domain.Vagrant.Enums.VagrantReplicaState.NotCreated,
                Labels = replicaLabels
            });
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

        await Cli.RunListenAndLog(_logger, $"vagrant destroy -f", dir, stoppingToken: cancellationToken);

        dir.DeleteDirectory();
    }
}
