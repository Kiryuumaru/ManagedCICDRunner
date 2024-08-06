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

    public async Task<VagrantBuild[]> GetBuilds()
    {
        List<VagrantBuild> vagrantBuilds = [];

        return [.. vagrantBuilds];
    }

    public async Task DeleteBuild(params string[] ids)
    {

    }

    public async Task Build(string id, string rev, string vagrantfile, CancellationToken cancellationToken)
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

        await Cli.RunListenAndLog(_logger, $"vagrant up", boxPath, stoppingToken: cancellationToken);
        await Cli.RunListenAndLog(_logger, $"vagrant package --output \"{packageBoxPath}\"", boxPath, stoppingToken: cancellationToken);
        await Cli.RunListenAndLog(_logger, $"vagrant box add {packageBoxPath} --name {id} -f", boxPath, stoppingToken: cancellationToken);
        await Cli.RunListenAndLog(_logger, $"vagrant destroy -f", boxPath, stoppingToken: cancellationToken);

        await revPath.WriteAllTextAsync(rev, cancellationToken);
    }

    public async Task<VagrantReplica[]> GetReplicas()
    {
        List<VagrantReplica> vagrantReplicas = [];

        foreach (var dir in ReplicaPath.GetDirectories())
        {
            AbsolutePath vagrantfilePath = dir / "Vagrantfile";
            AbsolutePath replicaFilePath = dir / "replica.json";
            if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() || await replicaFilePath.ReadObjAsync<JsonDocument>() is not JsonDocument replicaJson)
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

    public async Task DeleteReplica(params string[] ids)
    {
        foreach (var id in ids)
        {
            AbsolutePath dir = ReplicaPath / id;
            AbsolutePath vagrantfilePath = dir / "Vagrantfile";
            AbsolutePath replicaFilePath = dir / "replica.json";
            if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists())
            {
                continue;
            }

            await Cli.RunListenAndLog(_logger, $"vagrant destroy -f", dir);

            dir.DeleteDirectory();
        }
    }

    public async Task Run(string buildId, string replicaId, string rev, int cpus, int memoryGB, string? input, Dictionary<string, string> labels)
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

        await replicaFilePath.WriteObjAsync(replicaObj);

        await vagrantfilePath.WriteObjAsync($"""
            Vagrant.configure("2") do |config|
              config.vm.box = "{buildId}"
            end
            """);

        await Cli.RunListenAndLog(_logger, $"vagrant up", dir);
    }
}
