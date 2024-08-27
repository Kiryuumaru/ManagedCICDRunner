using Application.Common;
using Application.Vagrant.Services;
using Domain.Runner.Enums;
using Domain.Vagrant.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Vagrant.Models;

public class VagrantBuilder(VagrantService vagrantService)
{
    private readonly VagrantService _vagrantService = vagrantService;

    public List<(string buildId, Func<Task<string>> vagrantfileContentFactory)> DependentBuilds { get; private set; } = [];

    public Func<Task<string>>? VagrantfileContentFactory { get; private set; }

    public RunnerOSType? RunnerOSType { get; private set; }

    public int Cpus { get; private set; } = 2;

    public int MemoryGB { get; private set; } = 4;

    public VagrantBuilder AddDependentBuild(string buildId, Func<Task<string>> vagrantfileContentFactory)
    {
        DependentBuilds.Add((buildId, vagrantfileContentFactory));
        return this;
    }

    public VagrantBuilder AddDependentBuild(string buildId, string vagrantfileContent)
    {
        AddDependentBuild(buildId, () => Task.FromResult(vagrantfileContent));
        return this;
    }

    public VagrantBuilder AddDependentBuild(string baseBuildId, string buildId, Func<Task<string>> inputScriptFactory)
    {
        AddDependentBuild(buildId, async () =>
        {
            string vmGuest;
            string vmCommunicator;
            string vagrantSyncFolder;

            if (RunnerOSType == Domain.Runner.Enums.RunnerOSType.Linux)
            {
                vmGuest = ":linux";
                vmCommunicator = "ssh";
                vagrantSyncFolder = "/vagrant";
            }
            else if (RunnerOSType == Domain.Runner.Enums.RunnerOSType.Windows)
            {
                vmGuest = ":windows";
                vmCommunicator = "winssh";
                vagrantSyncFolder = "C:/vagrant";
            }
            else
            {
                throw new NotSupportedException();
            }

            var inputScript = await inputScriptFactory();

            return $"""
                Vagrant.configure("2") do |config|
                    config.vm.box = "{baseBuildId}"
                    config.vm.guest = {vmGuest}
                    config.vm.boot_timeout = 1800
                    config.vm.communicator = "{vmCommunicator}"
                    config.vm.synced_folder ".", "{vagrantSyncFolder}", disabled: true
                    config.vm.network "public_network", bridge: "Default Switch"
                    config.vm.provider "hyperv" do |hv|
                        hv.enable_virtualization_extensions = true
                        hv.ip_address_timeout = 900
                    end
                    config.vm.provision "shell", inline: <<-SHELL
                {inputScript}
                    SHELL
                end
                """;
        });
        return this;
    }

    public VagrantBuilder AddDependentBuild(string baseBuildId, string buildId, string inputScript)
    {
        AddDependentBuild(baseBuildId, buildId, () => Task.FromResult(inputScript));
        return this;
    }

    public VagrantBuilder SetRunnerOS(RunnerOSType runnerOSType)
    {
        RunnerOSType = runnerOSType;
        return this;
    }

    public VagrantBuilder SetCpus(int cpus)
    {
        Cpus = cpus;
        return this;
    }

    public VagrantBuilder SetMemoryGB(int memoryGB)
    {
        MemoryGB = memoryGB;
        return this;
    }

    public async Task Boot(string replicaId, string replicaRev, CancellationToken cancellationToken)
    {
        if (RunnerOSType == null)
        {
            throw new Exception("runnerOSType is not set");
        }

        string currentRev = "base";
        string? currentBuildId = null;
        foreach (var (buildId, vagrantfileContentFactory) in vagrantBuilder.DependentBuilds)
        {
            currentBuildId = buildId;
            var build = await Build(buildId, currentRev, vagrantfileContentFactory, cancellationToken);
            currentRev = $"{build.VagrantFileHash}-base_hash";
        }

        if (currentBuildId == null)
        {
            throw new Exception("no base vagrant build configured");
        }

        await Run(vagrantBuilder.RunnerOSType.Value, currentBuildId, replicaId, replicaRev, vagrantBuilder.Cpus, vagrantBuilder.MemoryGB, [], cancellationToken);

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

        var ss = $"""
                Vagrant.configure("2") do |config|
                  config.vm.box = "{buildId}"
                  config.vm.guest = {vmGuest}
                  config.vm.boot_timeout = 1800
                  config.vm.communicator = "{vmCommunicator}"
                  config.vm.synced_folder ".", "{vagrantSyncFolder}", disabled: true
                  config.vm.network "public_network", bridge: "Default Switch"
                  config.vm.provider "hyperv" do |hv|
                    hv.enable_virtualization_extensions = true
                    hv.ip_address_timeout = 900
                    hv.linked_clone = true
                    hv.memory = "{1024 * memoryGB}"
                    hv.cpus = "{cpus}"
                  end
                  config.vm.provision "shell", inline: <<-SHELL
                  SHELL
                end
                """);
        return _vagrantService.Boot(this, replicaId, replicaRev, cancellationToken);
    }
}
