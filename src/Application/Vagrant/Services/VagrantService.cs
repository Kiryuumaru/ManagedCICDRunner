﻿using Application.Common;
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
    private static readonly AbsolutePath TempPath = DataPath / "temp";

    public async Task<VagrantBuild[]> GetBuilds(CancellationToken cancellationToken)
    {
        List<VagrantBuild> vagrantBuilds = [];

        return [.. vagrantBuilds];
    }

    public async Task DeleteBuild(string id, CancellationToken cancellationToken)
    {

    }

    public async Task Build(string id, string rev, string vagrantfile, AbsolutePath[] hostAssets, CancellationToken cancellationToken)
    {
        AbsolutePath boxPath = BuildPath / id;
        AbsolutePath revPath = boxPath / "rev";

        string? currentRev = null;
        if (revPath.FileExists())
        {
            currentRev = await revPath.ReadAllTextAsync(cancellationToken);
        }

        if (currentRev == rev)
        {
            return;
        }

        AbsolutePath packageBoxPath = boxPath / "package.box";
        AbsolutePath vagrantfilePath = boxPath / "Vagrantfile";

        await vagrantfilePath.WriteAllTextAsync(vagrantfile, cancellationToken);

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
        await Cli.RunListenAndLog(_logger, $"vagrant box add {packageBoxPath} --name {id} -f", boxPath, stoppingToken: cancellationToken);
        await Cli.RunListenAndLog(_logger, $"vagrant destroy -f", boxPath, stoppingToken: cancellationToken);

        await revPath.WriteAllTextAsync(rev, cancellationToken);
    }

    public async Task Build(RunnerOSType runnerOSType, string baseBuildId, string buildId, string rev, string inputScript, AbsolutePath[] hostAssets, CancellationToken cancellationToken)
    {
        AbsolutePath boxPath = BuildPath / buildId;
        AbsolutePath revPath = boxPath / "rev";

        string? currentRev = null;
        if (revPath.FileExists())
        {
            currentRev = await revPath.ReadAllTextAsync(cancellationToken);
        }

        if (currentRev == rev)
        {
            return;
        }

        AbsolutePath bootstrapPath;
        string vagrantfile = $"""
            Vagrant.configure("2") do |config|
              config.vm.box = "{baseBuildId}"
            """;
        if (runnerOSType == RunnerOSType.Linux)
        {
            bootstrapPath = TempPath / buildId / "bootstrap.sh";
            vagrantfile += $"""

                  config.vm.provision "shell", path: "/vagrant/assets/bootstrap.sh"
                """;
        }
        else if (runnerOSType == RunnerOSType.Windows)
        {
            bootstrapPath = TempPath / buildId / "bootstrap.ps1";
            vagrantfile += $"""

                  config.vm.provision "shell", path: "C:/vagrant/assets/bootstrap.ps1"
                """;
        }
        else
        {
            throw new NotSupportedException();
        }
        await bootstrapPath.WriteAllTextAsync(inputScript, cancellationToken);
        vagrantfile += $"""

            end
            """;

        hostAssets = [.. hostAssets, .. new AbsolutePath[] { bootstrapPath }];

        await Build(buildId, rev, vagrantfile, hostAssets, cancellationToken);
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

            string id = dir.Stem;
            string rev = replicaJson.RootElement.GetProperty("rev").GetString()!;
            var labels = replicaJson.RootElement.GetProperty("labels").EnumerateObject().ToDictionary(i => i.Name, i => i.Value.GetString()!)!;

            vagrantReplicas.Add(new()
            {
                Id = id,
                Rev = rev,
                State = Domain.Vagrant.Enums.VagrantReplicaState.NotCreated,
                Labels = labels
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

    public async Task Run(string buildId, string replicaId, string rev, int cpus, int memoryGB, string? input, Dictionary<string, string> labels, CancellationToken cancellationToken)
    {
        var replicaObj = new
        {
            rev,
            labels
        };

        AbsolutePath dir = ReplicaPath / replicaId;
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath replicaFilePath = dir / "replica.json";

        if (dir.DirectoryExists() || dir.FileExists())
        {
            throw new Exception($"Error running vagrant replica \"{replicaId}\": Replica already exists");
        }

        await replicaFilePath.WriteObjAsync(replicaObj, cancellationToken: cancellationToken);

        await vagrantfilePath.WriteObjAsync($"""
            Vagrant.configure("2") do |config|
              config.vm.box = "{buildId}"
            end
            """, cancellationToken: cancellationToken);

        await Cli.RunListenAndLog(_logger, $"vagrant up", dir, stoppingToken: cancellationToken);
    }
}
