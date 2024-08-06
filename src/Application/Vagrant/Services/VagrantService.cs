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
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.Vagrant.Services;

public class VagrantService(ILogger<VagrantService> logger)
{
    private readonly ILogger<VagrantService> _logger = logger;

    private static readonly AbsolutePath DataPath = Defaults.DataPath / "vagrant";
    private static readonly AbsolutePath BoxPath = DataPath / "box";
    private static readonly AbsolutePath ReplicaPath = DataPath / "replica";

    public async Task<VagrantInstance[]> GetInstances()
    {
        List<VagrantInstance> vagrantInstances = [];


        return [.. vagrantInstances];
    }

    public async Task DeleteInstance(params string[] ids)
    {
    }

    public async Task<VagrantBox[]> GetBoxes()
    {
        List<VagrantBox> vagrantBoxes = [];

        return [.. vagrantBoxes];
    }

    public async Task DeleteBox(params string[] images)
    {

    }

    public async Task Build(string id, string rev, string vagrantfile, CancellationToken cancellationToken)
    {
        AbsolutePath boxPath = BoxPath / id;
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

    public async Task Run(string id, string runnerId, int cpus, int memoryGB, string? input)
    {

    }
}
