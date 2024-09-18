using AbsolutePathHelpers;
using Application.Common;
using Application.Configuration.Extensions;
using Application.Runner.Services;
using CliWrap.EventStream;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Runner.Models;
using Domain.Vagrant.Enums;
using Domain.Vagrant.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using System.Xml.Linq;
using TransactionHelpers;

namespace Application.Vagrant.Services;

public class VagrantService(ILogger<VagrantService> logger, IServiceProvider serviceProvider, IConfiguration configuration)
{
    private readonly ILogger<VagrantService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    private readonly Uri _clientInstallerUri = new("https://releases.hashicorp.com/vagrant/2.4.1/vagrant_2.4.1_windows_amd64.msi");
    private readonly string _clientInstallerHash = "3dbd0f5a063e61e96560bc62f90f4071e1c6f4a2d39020cd162055fcf390a6d4d1b3b551a19224ba9f09ada17ef64cf0989ec2ddfb02bc32c67c7075272d2acf";

    private AbsolutePath DataPath => _configuration.GetDataPath() / "vagrant";

    private AbsolutePath BuildPath => DataPath / "build";

    private AbsolutePath ReplicaPath => DataPath / "replica";

    private AbsolutePath TempPath => DataPath / "temp";

    private AbsolutePath ClientPath => DataPath / "client";

    private AbsolutePath ClientExecPath => ClientPath / "Vagrant" / "bin" / "vagrant.exe";

    private AbsolutePath VagrantHomePath => DataPath / "data";

    private Dictionary<string, string?> VagrantEnvVars => new()
    {
        ["VAGRANT_HOME"] = VagrantHomePath,
        ["VAGRANT_DEFAULT_PROVIDER"] = "hyperv"
    };

    private readonly ExecutorLocker locker = new();

    public async Task VerifyClient(CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(new ()
        {
            ["Service"] = nameof(VagrantService),
            ["VagrantAction"] = nameof(VerifyClient)
        });

        await BuildPath.KillExceptHyperv(cancellationToken);
        await ReplicaPath.KillExceptHyperv(cancellationToken);
        await TempPath.KillExceptHyperv(cancellationToken);
        await VagrantHomePath.KillExceptHyperv(cancellationToken);

        await TempPath.Delete(cancellationToken);

        _logger.LogInformation("Verifying vagrant client...");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["version"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);
                break;
            }
            catch
            {
                _logger.LogInformation("Vagrant client is not installed. Installing vagrant client...");
            }

            try
            {
                _logger.LogDebug("Downloading vagrant client installer...");

                using var httpClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();

                var clientInstallerDownloadPath = TempPath / "vagrant_windows_amd64.msi";

                await ClientPath.Delete(cancellationToken);
                await clientInstallerDownloadPath.Delete(cancellationToken);

                ClientPath.CreateDirectory();
                clientInstallerDownloadPath.Parent?.CreateDirectory();

                {
                    using var response = await httpClient.GetAsync(_clientInstallerUri, cancellationToken);
                    using var fileStream = new FileStream(clientInstallerDownloadPath, FileMode.CreateNew);
                    await response.Content.CopyToAsync(fileStream, cancellationToken);
                }

                if (await clientInstallerDownloadPath.GetHashSHA512(cancellationToken) != _clientInstallerHash)
                {
                    throw new Exception("Downloaded vagrant file is corrupted");
                }

                _logger.LogDebug("Downloading vagrant client installer done");

                _logger.LogDebug("Installing vagrant client...");

                await Cli.RunOnce("powershell", ["-c", $"Start-Process msiexec.exe -Wait -ArgumentList /a,\\\"{clientInstallerDownloadPath}\\\",/qn,TARGETDIR=\\\"{ClientPath}\\\""], TempPath, stoppingToken: cancellationToken);

                if (!ClientExecPath.FileExists())
                {
                    throw new Exception("Vagrant client was not installed");
                }

                _logger.LogInformation("Installing vagrant client done");
            }
            catch (Exception ex)
            {
                _logger.LogError("Error installing vagrant client: {err}", ex.Message);

                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.LogDebug("Patching ssh keys permissions...");
        await WindowsOSHelpers.TakeOwnPermission(VagrantHomePath / "insecure_private_key", cancellationToken);
        foreach (var keyPath in (VagrantHomePath / "insecure_private_keys").GetFiles())
        {
            await WindowsOSHelpers.TakeOwnPermission(keyPath, cancellationToken);
        }
        _logger.LogDebug("Patching ssh keys permissions done");

        _logger.LogInformation("Vagrant client verified");
    }

    public async Task<VagrantBuild> Build(RunnerOSType runnerOSType, string baseBuildId, string buildId, string rev, int? changeStorageGB, Func<Task<string>> provisionScriptFactory, CancellationToken cancellationToken)
    {
        AbsolutePath boxPath = BuildPath / buildId;
        AbsolutePath buildFilePath = boxPath / "build.json";
        AbsolutePath packageBoxPath = boxPath / "package.box";
        AbsolutePath vagrantfilePath = boxPath / "Vagrantfile";
        AbsolutePath boxPathTemp = TempPath / $"{buildId}-{Guid.NewGuid()}";
        AbsolutePath vagrantfilePathTemp = boxPathTemp / "Vagrantfile";

        string? currentVagrantFileHash = null;
        string? currentRev = null;
        if (buildFilePath.FileExists() &&
            await buildFilePath.Read<VagrantBuild>(JsonSerializerExtension.CamelCaseOption, cancellationToken: cancellationToken) is VagrantBuild vagrantBuild)
        {
            currentVagrantFileHash = vagrantBuild.VagrantFileHash;
            currentRev = vagrantBuild.Rev;
        }

        var hostPublicKey = boxPath / "public_key";
        var hostPrivateKey = boxPath / "private_key";

        string vmGuest;
        string vmCommunicator;
        string guestSyncFolder;
        string guestPublicTmpKey;
        string guestAppendKeys;
        string guestAppendRootKeys;
        string guestSshAuthorizedKeysPath;
        string guestRootSshAuthorizedKeysPath;
        if (runnerOSType == RunnerOSType.Linux)
        {
            vmGuest = ":linux";
            vmCommunicator = "ssh";
            guestSyncFolder = "/vagrant";
            guestSshAuthorizedKeysPath = "/home/vagrant/.ssh/authorized_keys";
            guestRootSshAuthorizedKeysPath = "/root/.ssh/authorized_keys";
            guestPublicTmpKey = $"/tmp/public_key";
            guestAppendKeys = $"echo \"\\n$(cat {guestPublicTmpKey})\" >> {guestSshAuthorizedKeysPath}";
            guestAppendRootKeys = $"echo \"\\n$(cat {guestPublicTmpKey})\" >> {guestRootSshAuthorizedKeysPath}";
        }
        else if (runnerOSType == RunnerOSType.Windows)
        {
            vmGuest = ":windows";
            vmCommunicator = "winssh";
            guestSyncFolder = "C:\\vagrant";
            guestSshAuthorizedKeysPath = "C:\\Users\\vagrant\\.ssh\\authorized_keys";
            guestRootSshAuthorizedKeysPath = "C:\\ProgramData\\ssh\\administrators_authorized_keys";
            guestPublicTmpKey = $"C:\\tmp\\public_key";
            guestAppendKeys = $"Get-Content {guestPublicTmpKey.Replace("\\", "\\\\")} | Add-Content {guestSshAuthorizedKeysPath.Replace("\\", "\\\\")}";
            guestAppendRootKeys = $"Get-Content {guestPublicTmpKey.Replace("\\", "\\\\")} | Add-Content {guestRootSshAuthorizedKeysPath.Replace("\\", "\\\\")}";
        }
        else
        {
            throw new NotSupportedException();
        }

        string provisionScript = await provisionScriptFactory();
        string vagrantFileTemplate = $"""
            Vagrant.configure("2") do |config|
                config.vm.box = "{baseBuildId}"
                config.vm.guest = {vmGuest}
                config.vm.communicator = "{vmCommunicator}"
                config.vm.synced_folder ".", "{guestSyncFolder}", disabled: true
                config.vm.network "public_network", bridge: "Default Switch"
                config.vm.box_check_update = false
                config.vm.boot_timeout = 1800
                __ADDITIONAL_CONFIG__
                config.vm.provider "hyperv" do |hv|
                    hv.ip_address_timeout = 300
                    hv.enable_virtualization_extensions = true
                end
                config.vm.provision "file", source: "{hostPublicKey.ToString().Replace("\\", "\\\\")}", destination: "{guestPublicTmpKey.Replace("\\", "\\\\")}"
                config.vm.provision "shell", inline: <<-SHELL
                    {guestAppendKeys}
                    {guestAppendRootKeys}

                    {provisionScript.Replace(Environment.NewLine, $"{Environment.NewLine}        ")}

                SHELL
            end
            """;
        string vagrantFileInitial = vagrantFileTemplate.Replace("__ADDITIONAL_CONFIG__", "config.ssh.insert_key = true");
        string vagrantFileFinal = vagrantFileTemplate.Replace("__ADDITIONAL_CONFIG__", $"config.ssh.insert_key = false\n    config.ssh.private_key_path = [\"{hostPrivateKey.ToString().Replace("\\", "\\\\")}\"]");

        await vagrantfilePathTemp.WriteAllText(vagrantFileFinal, cancellationToken);
        string vagrantFileHash = await vagrantfilePathTemp.GetHashSHA512(cancellationToken);
        await boxPathTemp.WaitKillAll([], cancellationToken.WithTimeout(TimeSpan.FromMinutes(2)));

        VagrantBuild newVagrantBuild = new()
        {
            Id = buildId,
            VagrantFileHash = vagrantFileHash,
            RunnerOS = runnerOSType,
            Rev = rev
        };

        if (vagrantFileHash == currentVagrantFileHash && currentRev == rev)
        {
            return newVagrantBuild;
        }

        await locker.Execute(buildId, async () => {

            if (boxPath.DirectoryExists())
            {
                await DeleteCore(boxPath, null, cancellationToken);
            }

            using var _ = _logger.BeginScopeMap(new ()
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(Build),
                ["VagrantBuildId"] = buildId,
                ["VagrantBuildRev"] = rev,
                ["VagrantFileHash"] = vagrantFileHash
            });

            try
            {
                _logger.LogInformation("Starting vagrant build {VagrantBuildId}", buildId);

                _logger.LogDebug("Generating initial vagrantfile {VagrantBuildId}", buildId);
                await vagrantfilePath.WriteAllText(vagrantFileInitial, cancellationToken);

                _logger.LogDebug("Generating ssh keys {VagrantBuildId}", buildId);
                await GenerateSshKeys(buildId, boxPath, cancellationToken);

                await locker.Execute(baseBuildId, async () =>
                {
                    _logger.LogDebug("Starting vagrant VM {VagrantBuildId}", buildId);
                    await Cli.RunListenAndLog(_logger, ClientExecPath, ["up", "--no-provision", "--provider", "hyperv"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);
                });

                var vmName = await GetVMName(buildId, cancellationToken);
                if (string.IsNullOrEmpty(vmName))
                {
                    throw new Exception($"VM {buildId} was not started");
                }
                _logger.LogDebug("VM {VagrantBuildVMName} was started", vmName);

                if (changeStorageGB.HasValue)
                {
                    await ResizeGuestVMStorage(boxPath, vmName, runnerOSType, changeStorageGB.Value, cancellationToken);
                }

                _logger.LogDebug("Building vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["up", "--provision", "--provider", "hyperv"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                _logger.LogDebug("Reloading vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["reload"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                _logger.LogDebug("Generating final vagrantfile {VagrantBuildId}", buildId);
                await vagrantfilePath.WriteAllText(vagrantFileFinal, cancellationToken);

                _logger.LogDebug("Packaging vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["package", "--output", packageBoxPath], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                _logger.LogDebug("Saving vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["box", "add", packageBoxPath, "--name", buildId, "-f"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                var generatedKeyPath = VagrantHomePath / "boxes" / buildId / "0" / "hyperv" / "vagrant_private_key";
                if (generatedKeyPath.FileExists())
                {
                    _logger.LogDebug("Patching ssh keys permissions {VagrantBuildId}", buildId);
                    await WindowsOSHelpers.TakeOwnPermission(generatedKeyPath, cancellationToken);
                }

                _logger.LogDebug("Cleaning vagrant build {VagrantBuildId}", buildId);
                await DeleteVMCore(boxPath, vmName, cancellationToken);

                await buildFilePath.Write(newVagrantBuild, JsonSerializerExtension.CamelCaseOption, cancellationToken: cancellationToken);

                _logger.LogInformation("Vagrant build was built {VagrantBuildId}", buildId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on building vagrant build {VagrantBuildId}: {ErrorMessage}", buildId, ex.Message);
                throw;
            }
        });

        return newVagrantBuild;
    }

    public async Task<VagrantBuild?> GetBuild(string id, CancellationToken cancellationToken)
    {
        AbsolutePath dir = BuildPath / id;
        AbsolutePath packageBoxPath = dir / "package.box";
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath buildFilePath = dir / "build.json";

        if (!vagrantfilePath.FileExists() || !packageBoxPath.FileExists() || !buildFilePath.FileExists() ||
            await buildFilePath.Read<VagrantBuild>(JsonSerializerExtension.CamelCaseOption, cancellationToken: cancellationToken) is not VagrantBuild vagrantBuild)
        {
            return null;
        }

        return vagrantBuild;
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
            if (await GetBuild(buildId, cancellationToken) is VagrantBuild vagrantBuild)
            {
                buildId = vagrantBuild.Id;
            }
        }
        catch { }

        await locker.Execute(buildId, async () =>
        {
            using var _ = _logger.BeginScopeMap(new ()
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(DeleteBuild),
                ["VagrantBuildId"] = buildId
            });

            _logger.LogInformation("Deleting vagrant build {VagrantBuildId}", buildId);

            try
            {
                await DeleteCore(boxPath, null, cancellationToken);
            }
            catch { }
            try
            {
                await Cli.RunOnce(ClientExecPath, ["box", "remove", buildId, "-f"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);
            }
            catch { }

            _logger.LogInformation("Deleted vagrant build {VagrantBuildId}", buildId);
        });
    }

    public async Task Run(string buildId, string replicaId, string rev, int cpus, int memoryGB, int storageGB, Dictionary<string, string> labels, CancellationToken cancellationToken)
    {
        AbsolutePath replicaPath = ReplicaPath / replicaId;
        AbsolutePath vagrantfilePath = replicaPath / "Vagrantfile";
        AbsolutePath replicaFilePath = replicaPath / "replica.json";

        if (replicaPath.DirectoryExists() || replicaPath.FileExists())
        {
            throw new Exception($"Error running vagrant replica \"{replicaId}\": Replica already exists");
        }

        var vagrantBuild = await GetBuild(buildId, cancellationToken) ??
            throw new Exception($"Error running vagrant replica \"{replicaId}\": BuildId {buildId} does not exists");

        var hostPrivateKey = BuildPath / buildId / "private_key";

        string vmGuest;
        string vmCommunicator;
        string vagrantSyncFolder;
        if (vagrantBuild.RunnerOS == RunnerOSType.Linux)
        {
            vmGuest = ":linux";
            vmCommunicator = "ssh";
            vagrantSyncFolder = "/vagrant";
        }
        else if (vagrantBuild.RunnerOS == RunnerOSType.Windows)
        {
            vmGuest = ":windows";
            vmCommunicator = "winssh";
            vagrantSyncFolder = "C:/vagrant";
        }
        else
        {
            throw new NotSupportedException();
        }

        string vagrantFile = $"""
            Vagrant.configure("2") do |config|
                config.vm.box = "{buildId}"
                config.vm.guest = {vmGuest}
                config.vm.communicator = "{vmCommunicator}"
                config.vm.synced_folder ".", "{vagrantSyncFolder}", disabled: true
                config.vm.network "public_network", bridge: "Default Switch"
                config.vm.box_check_update = false
                config.vm.boot_timeout = 1800
                config.ssh.insert_key = false
                config.ssh.private_key_path = ["{hostPrivateKey.ToString().Replace("\\", "\\\\")}"]
                config.vm.provider "hyperv" do |hv|
                    hv.ip_address_timeout = 300
                    hv.enable_virtualization_extensions = true
                    hv.linked_clone = true
                    hv.memory = "{1024 * memoryGB}"
                    hv.cpus = "{cpus}"
                end
                config.vm.provision "shell", inline: <<-SHELL
                SHELL
            end
            """;

        string? vmName = null;

        await locker.Execute(replicaId, async () =>
        {
            using var _ = _logger.BeginScopeMap(new()
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(Run),
                ["VagrantBuildId"] = buildId,
                ["VagrantReplicaId"] = replicaId,
                ["VagrantReplicaRev"] = rev,
            });

            try
            {
                await locker.Execute(buildId, async () =>
                {
                    _logger.LogInformation("Starting vagrant replica {VagrantReplicaId}", replicaId);

                    VagrantReplica vagrantReplica = new()
                    {
                        BuildId = buildId,
                        Id = replicaId,
                        Rev = rev,
                        Cpus = cpus,
                        MemoryGB = memoryGB,
                        StorageGB = storageGB,
                        VMName = null,
                        Labels = labels
                    };

                    await replicaFilePath.Write(vagrantReplica, JsonSerializerExtension.CamelCaseOption, cancellationToken: cancellationToken);
                    await vagrantfilePath.WriteAllText(vagrantFile, cancellationToken: cancellationToken);

                    await Cli.RunListenAndLog(_logger, ClientExecPath, ["up", "--no-provision", "--provider", "hyperv"], replicaPath, VagrantEnvVars, stoppingToken: cancellationToken);
                });

                vmName = await GetVMName(replicaId, cancellationToken);

                if (string.IsNullOrEmpty(vmName))
                {
                    throw new Exception($"{vmName} was not started");
                }

                await ResizeGuestVMStorage(replicaPath, vmName, vagrantBuild.RunnerOS, storageGB, cancellationToken);

                await Cli.RunListenAndLog(_logger, ClientExecPath, ["up", "--provision", "--provider", "hyperv"], replicaPath, VagrantEnvVars, stoppingToken: cancellationToken);

                VagrantReplica updatedVagrantReplica = new()
                {
                    BuildId = buildId,
                    Id = replicaId,
                    Rev = rev,
                    Cpus = cpus,
                    MemoryGB = memoryGB,
                    StorageGB = storageGB,
                    VMName = vmName,
                    Labels = labels
                };
                await replicaFilePath.Write(updatedVagrantReplica, JsonSerializerExtension.CamelCaseOption, cancellationToken: cancellationToken);

                _logger.LogInformation("Vagrant replica {VagrantReplicaId} is running", replicaId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on starting vagrant replica {VagrantReplicaId}: {ErrorMessage}", replicaId, ex.Message);
                throw;
            }
        });
    }

    public async Task Execute(string replicaId, Func<Task<string>> inputScriptFactory, CancellationToken cancellationToken)
    {
        AbsolutePath replicaPath = ReplicaPath / replicaId;
        AbsolutePath vagrantfilePath = replicaPath / "Vagrantfile";
        AbsolutePath replicaFilePath = replicaPath / "replica.json";

        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() ||
            await replicaFilePath.Read<VagrantReplica>(JsonSerializerExtension.CamelCaseOption, cancellationToken: cancellationToken) is not VagrantReplica vagrantReplica ||
            await GetBuild(vagrantReplica.BuildId, cancellationToken) is not VagrantBuild vagrantBuild)
        {
            return;
        }

        bool isLocked = false;
        var executeTask = Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(new ()
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(Execute),
                ["VagrantBuildId"] = vagrantBuild.Id,
                ["VagrantReplicaId"] = vagrantReplica.Id,
                ["VagrantReplicaRev"] = vagrantReplica.Rev
            });

            try
            {
                _logger.LogInformation("Executing a script on vagrant replica {VagrantReplicaId}", vagrantReplica.Id);
                
                while (!isLocked)
                {
                    await Task.Delay(500, cancellationToken);
                }

                CancellationTokenSource ctx = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var ct = ctx.Token;
                List<Task> tasks = [];

                tasks.Add(Task.Run(async () =>
                {
                    await foreach (var cmdEvent in Cli.RunListen(ClientExecPath, [$"ssh -c \"{NormalizeScriptInput(vagrantBuild.RunnerOS, await inputScriptFactory())}\""], replicaPath, VagrantEnvVars, stoppingToken: ct))
                    {
                        switch (cmdEvent)
                        {
                            case StandardOutputCommandEvent stdOut:
                                _logger.LogTrace("{x}", stdOut.Text);
                                break;
                            case StandardErrorCommandEvent stdErr:
                                _logger.LogTrace("{x}", stdErr.Text);
                                break;
                            case ExitedCommandEvent exited:
                                var msg = $"vagrant ssh ended with return code {exited.ExitCode}";
                                if (exited.ExitCode != 0)
                                {
                                    throw new Exception(msg);
                                }
                                else
                                {
                                    _logger.LogTrace("{x}", msg);
                                }
                                break;
                        }
                    }
                    if (!ct.IsCancellationRequested)
                    {
                        ctx.Cancel();
                    }

                }, ct));

                tasks.Add(Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && await GetStateCore(replicaPath, vagrantReplica.VMName, cancellationToken) == VagrantReplicaState.Running)
                    {
                        await Task.Delay(2000);
                    }
                    if (!ct.IsCancellationRequested)
                    {
                        ctx.Cancel();
                    }
                }, ct));

                await Task.WhenAny(tasks);

                _logger.LogInformation("Script execution done on vagrant replica {VagrantReplicaId}", replicaId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on executing a script on vagrant replica {VagrantReplicaId}: {ErrorMessage}", replicaId, ex.Message);
                throw;
            }
        }, cancellationToken);

        await locker.Execute(replicaId, async () =>
        {
            isLocked = true;
            DateTimeOffset toEnd = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (toEnd >= DateTimeOffset.UtcNow && !executeTask.IsCompleted)
            {
                await Task.Delay(1000, cancellationToken);
            }
        });

        try
        {
            await executeTask;
        }
        catch (OperationCanceledException) { }
    }

    public async Task<VagrantReplicaRuntime?> GetReplica(string id, CancellationToken cancellationToken)
    {
        AbsolutePath dir = ReplicaPath / id;
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath replicaFilePath = dir / "replica.json";

        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() ||
            await replicaFilePath.Read<VagrantReplica>(JsonSerializerExtension.CamelCaseOption, cancellationToken: cancellationToken) is not VagrantReplica vagrantReplica ||
            await GetBuild(vagrantReplica.BuildId, cancellationToken) is null)
        {
            return null;
        }

        VagrantReplicaState vagrantReplicaState = await GetStateCore(dir, vagrantReplica.VMName, cancellationToken);

        return new()
        {
            BuildId = vagrantReplica.BuildId,
            Id = vagrantReplica.Id,
            Rev = vagrantReplica.Rev,
            VMName = vagrantReplica.VMName,
            Cpus = vagrantReplica.Cpus,
            MemoryGB = vagrantReplica.MemoryGB,
            StorageGB = vagrantReplica.StorageGB,
            State = vagrantReplicaState,
            Labels = vagrantReplica.Labels
        };
    }

    public async Task<Dictionary<string, VagrantReplicaRuntime?>> GetReplicas(CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, VagrantReplicaRuntime?> vagrantReplicas = [];
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
            using var _ = _logger.BeginScopeMap(new ()
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(DeleteReplica),
                ["VagrantReplicaId"] = id
            });

            var replica = await GetReplica(id, cancellationToken);

            _logger.LogInformation("Deleting vagrant replica {VagrantReplicaId}", id);

            try
            {
                await DeleteCore(dir, replica?.VMName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on deleting a vagrant replica {VagrantReplicaId}: {ErrorMessage}", id, ex.Message);
                throw;
            }

            _logger.LogInformation("Deleted vagrant replica {VagrantReplicaId}", id);
        });
    }

    public async Task DeleteCore(AbsolutePath dir, string? vmName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!dir.DirectoryExists())
                {
                    break;
                }

                await DeleteVMCore(dir, vmName, cancellationToken.WithTimeout(TimeSpan.FromMinutes(2)));

                await dir.WaitKillExceptHyperv(cancellationToken.WithTimeout(TimeSpan.FromMinutes(2)));

                if ((dir / "Vagrantfile").DirectoryExists())
                {
                    await DeleteVagrant(dir, cancellationToken.WithTimeout(TimeSpan.FromMinutes(2)));
                }

                await dir.WaitKillExceptHyperv(cancellationToken.WithTimeout(TimeSpan.FromMinutes(2)));

                await dir.Delete(cancellationToken);

                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error on deleting {VagrantName}: {ErrorMessage}. retrying...", dir.Name, ex.Message);
            }
        }
    }

    public async Task DeleteVagrant(AbsolutePath dir, CancellationToken cancellationToken)
    {
        await foreach (var cmdEvent in Cli.RunListen(ClientExecPath, ["destroy", "-f"], dir, VagrantEnvVars, stoppingToken: cancellationToken))
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    _logger.LogTrace("{x}", stdOut.Text);
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
                        _logger.LogTrace("{x}", msg);
                    }
                    break;
            }
        }
    }

    public async Task DeleteVMCore(AbsolutePath dir, string? vmName, CancellationToken cancellationToken)
    {
        var id = dir.Name;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var ctxTimed = cancellationToken.WithTimeout(TimeSpan.FromSeconds(30));
                if (string.IsNullOrEmpty(vmName))
                {
                    vmName = await GetVMName(id, ctxTimed);
                }
                if (!string.IsNullOrEmpty(vmName))
                {
                    try
                    {
                        await Cli.RunOnce("powershell", ["Stop-VM", "-Name", vmName, "-TurnOff", "-Force"], environmentVariables: VagrantEnvVars, stoppingToken: ctxTimed);
                    }
                    catch { }
                    while (!ctxTimed.IsCancellationRequested)
                    {
                        var vmState = await GetStateCore(dir, vmName, ctxTimed);
                        if (vmState == VagrantReplicaState.NotCreated || vmState == VagrantReplicaState.Off)
                        {
                            break;
                        }
                    }
                    try
                    {
                        await Cli.RunOnce("powershell", ["Remove-VM", "-Name", vmName, "-Force"], environmentVariables: VagrantEnvVars, stoppingToken: ctxTimed);
                    }
                    catch { }
                    while (!ctxTimed.IsCancellationRequested)
                    {
                        var vmState = await GetStateCore(dir, vmName, ctxTimed);
                        if (vmState == VagrantReplicaState.NotCreated)
                        {
                            break;
                        }
                    }
                }

                var vagrantCreatedDir = dir / ".vagrant";

                await vagrantCreatedDir.WaitKillExceptHyperv(ctxTimed);

                if (ctxTimed.IsCancellationRequested)
                {
                    continue;
                }

                await vagrantCreatedDir.Delete(cancellationToken);

                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error on deleting VM {VagrantId}: {ErrorMessage}. retrying...", id, ex.Message);
            }
        }
    }

    private async Task<VagrantReplicaState> GetStateCore(AbsolutePath vagrantDir, string? vmName, CancellationToken cancellationToken)
    {
        VagrantReplicaState vagrantReplicaState = VagrantReplicaState.NotCreated;

        try
        {
            if (string.IsNullOrEmpty(vmName))
            {
                vmName = await GetVMName(vagrantDir.Name, cancellationToken);
            }
            if (!string.IsNullOrEmpty(vmName))
            {
                var vmState = (await Cli.RunOnce("powershell", [$"(Get-VM -Name \"{vmName}\").State"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken)).Trim();
                vagrantReplicaState = vmState.ToLowerInvariant() switch
                {
                    "off" => VagrantReplicaState.Off,
                    "stopping" => VagrantReplicaState.Off,
                    "saved" => VagrantReplicaState.Off,
                    "paused" => VagrantReplicaState.Off,
                    "reset" => VagrantReplicaState.Off,
                    "running" => VagrantReplicaState.Running,
                    "starting" => VagrantReplicaState.Starting,
                    _ => throw new NotImplementedException($"{vmState} is not implemented as VagrantReplicaState")
                };
            }
        }
        catch { }

        return vagrantReplicaState;
    }

    private async Task<string?> GetVMName(string id, CancellationToken cancellationToken)
    {
        string idStart = $"{id}_default";
        string? vmName = null;
        try
        {
            var rawGetVm = await Cli.RunOnce("powershell", ["Get-VM | ConvertTo-Json"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);
            var getVmJson = JsonSerializer.Deserialize<JsonDocument>(rawGetVm)!;
            JsonElement[] elements = [];
            if (getVmJson.RootElement.ValueKind == JsonValueKind.Array)
            {
                elements = [.. getVmJson.RootElement.EnumerateArray()];
            }
            else
            {
                elements = [getVmJson.RootElement];
            }
            foreach (var prop in elements)
            {
                if (prop!.GetProperty("Name").GetString() is string name &&
                    name.StartsWith(idStart, StringComparison.InvariantCultureIgnoreCase))
                {
                    vmName = name;
                    break;
                }
            }
        }
        catch { }
        return vmName;
    }

    private async Task ResizeGuestVMStorage(AbsolutePath vagrantDir, string vmName, RunnerOSType runnerOS, int storageGB, CancellationToken cancellationToken)
    {
        var currentVMState = await GetStateCore(vagrantDir, vmName, cancellationToken);
        if (currentVMState != VagrantReplicaState.Running)
        {
            throw new Exception($"Cannot resize storage for {vmName}, VM is not running");
        }

        var vmHardDisk = (vagrantDir / ".vagrant" / "machines" / "default" / "hyperv" / "Virtual Hard Disks").GetFiles("*.vhdx").FirstOrDefault() ??
            throw new Exception($"{vmName} hard disk was not found");

        var newStorageBytes = storageGB * 1024L * 1024L * 1024L;
        var vmHardDiskInfoRaw = await Cli.RunOnce("powershell", [$"Get-VHD -Path \\\"{vmHardDisk}\\\" | ConvertTo-Json"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);
        var vmHardDiskInfoJson = JsonSerializer.Deserialize<JsonDocument>(vmHardDiskInfoRaw)!;
        var currentVHDSizeBytes = vmHardDiskInfoJson.RootElement.GetProperty("Size").GetInt64();
        var currentVHDSizeGB = currentVHDSizeBytes / 1024.0 / 1024.0 / 1024.0;

        if (newStorageBytes == currentVHDSizeBytes)
        {
            _logger.LogDebug("Skipped {VagrantVMName} VHD resize.", vmName);
            return;
        }

        bool isUpsize = false;
        long sizeChangeBytes = newStorageBytes - currentVHDSizeBytes;
        if (sizeChangeBytes > 0)
        {
            isUpsize = true;
        }

        if (isUpsize)
        {
            _logger.LogDebug("Stopping VM {VagrantVMName} for VHD upsizing", vmName);
            await Cli.RunListenAndLog(_logger, "powershell", [$"Stop-VM -Name \\\"{vmName}\\\" -TurnOff -Force"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);

            _logger.LogDebug("Upsizing VM {VagrantVMName} VHD from {OldStorageSizeGB} GB to {NewStorageSizeGB} GB", vmName, currentVHDSizeGB, storageGB);
            await Cli.RunListenAndLog(_logger, "powershell", [$"Resize-VHD -Path \\\"{vmHardDisk}\\\" -SizeBytes \\\"{newStorageBytes}\\\""], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);

            _logger.LogDebug("Starting VM {VagrantVMName} with resized VHD", vmName);
            await Cli.RunListenAndLog(_logger, ClientExecPath, ["up", "--no-provision", "--provider", "hyperv"], vagrantDir, VagrantEnvVars, stoppingToken: cancellationToken);

            _logger.LogDebug("Expanding VM {VagrantVMName} primary partition", vmName);
            await Cli.RunListenAndLog(_logger, ClientExecPath, [$"ssh -c \"{NormalizeScriptInput(runnerOS, runnerOS switch {
                RunnerOSType.Linux => $"""
                    primaryPartition=$(grep -c 'sda[0-9]' /proc/partitions)
                    sudo growpart /dev/sda $primaryPartition
                    sudo pvresize /dev/sda$primaryPartition
                    sudo lvextend -l +100%FREE /dev/ubuntu-vg/ubuntu-lv
                    sudo resize2fs /dev/mapper/ubuntu--vg-ubuntu--lv
                    """,
                RunnerOSType.Windows => $"""
                    $ErrorActionPreference="Stop"; $verbosePreference="Continue"; $ProgressPreference = "SilentlyContinue"
                    $PrimaryPartition = (Get-Partition -DiskNumber 0).Count
                    $SizePart = Get-PartitionSupportedSize -DiskNumber 0 -PartitionNumber $PrimaryPartition
                    $SizeMax = $SizePart.SizeMax
                    Resize-Partition -DiskNumber 0 -PartitionNumber $PrimaryPartition -Size $SizeMax
                    """,
                _ => throw new NotSupportedException()
            })}\""], vagrantDir, VagrantEnvVars, stoppingToken: cancellationToken);
        }
        else
        {
            return;

            _logger.LogDebug("Shrinking VM {VagrantVMName} primary partition", vmName);
            await Cli.RunListenAndLog(_logger, ClientExecPath, [$"ssh -c \"{NormalizeScriptInput(runnerOS, runnerOS switch {
                RunnerOSType.Linux => $"""

                    """,
                RunnerOSType.Windows => $"""
                    $ErrorActionPreference="Stop"; $verbosePreference="Continue"; $ProgressPreference = "SilentlyContinue"
                    $PrimaryPartition = (Get-Partition -DiskNumber 0).Count
                    $SizePart = Get-PartitionSupportedSize -DiskNumber 0 -PartitionNumber $PrimaryPartition
                    echo $SizePart.SizeMax
                    echo $SizePart.SizeMax
                    echo $SizePart.SizeMax
                    echo $SizePart.SizeMax
                    echo $SizePart.SizeMax
                    echo $SizePart.SizeMax
                    echo $SizePart.SizeMax
                    $SizeTarget = $SizePart.SizeMax - ${Math.Abs(sizeChangeBytes)}
                    echo $SizeTarget
                    echo $SizeTarget
                    echo $SizeTarget
                    echo $SizeTarget
                    echo $SizeTarget
                    echo $SizeTarget
                    echo $SizeTarget
                    $SizeTargetNormalized = ($SizeTarget - ($SizeTarget % 1024))
                    echo $SizeTargetNormalized
                    echo $SizeTargetNormalized
                    echo $SizeTargetNormalized
                    echo $SizeTargetNormalized
                    echo $SizeTargetNormalized
                    echo $SizeTargetNormalized
                    echo $SizeTargetNormalized
                    echo $SizeTargetNormalized
                    Resize-Partition -DiskNumber 0 -PartitionNumber $PrimaryPartition -Size $SizeTargetNormalized
                    """,
                _ => throw new NotSupportedException()
            })}\""], vagrantDir, VagrantEnvVars, stoppingToken: cancellationToken);

            _logger.LogDebug("Stopping VM {VagrantVMName} for VHD downsizing", vmName);
            await Cli.RunListenAndLog(_logger, "powershell", [$"Stop-VM -Name \\\"{vmName}\\\" -TurnOff -Force"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);

            if (runnerOS == RunnerOSType.Windows)
            {
                _logger.LogDebug("Downsizing VM {VagrantVMName} VHD from {OldStorageSizeGB} GB to {NewStorageSizeGB} GB", vmName, currentVHDSizeGB, storageGB);
                await Cli.RunListenAndLog(_logger, "powershell", [$"Resize-VHD -Path \\\"{vmHardDisk}\\\" -ToMinimumSize"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);
            }

            _logger.LogDebug("Starting VM {VagrantVMName} with resized VHD", vmName);
            await Cli.RunListenAndLog(_logger, ClientExecPath, ["up", "--no-provision", "--provider", "hyperv"], vagrantDir, VagrantEnvVars, stoppingToken: cancellationToken);
        }
    }

    private async Task GenerateSshKeys(string id, AbsolutePath path, CancellationToken cancellationToken)
    {
        var keygen = new SshKeyGenerator.SshKeyGenerator(4096);
        var privateKey = keygen.ToPrivateKey();
        var publicKey = keygen.ToRfcPublicKey($"ManagedCICDRunner {id}");
        var privateKeyPath = path / "private_key";
        var publicKeyPath = path / "public_key";
        await privateKeyPath.WriteAllText(privateKey, cancellationToken);
        await publicKeyPath.WriteAllText(publicKey, cancellationToken);
        await WindowsOSHelpers.TakeOwnPermission(privateKeyPath, cancellationToken);
        await WindowsOSHelpers.TakeOwnPermission(publicKeyPath, cancellationToken);
    }

    private static string NormalizeScriptInput(RunnerOSType runnerOS, string input)
    {
        if (runnerOS == RunnerOSType.Linux)
        {
            return input.Replace("\n\r", " && ")
                .Replace("\r\n", " && ")
                .Replace("\n", " && ")
                .Replace("\"", "\\\"");
        }
        else if (runnerOS == RunnerOSType.Windows)
        {
            return input.Replace("\n\r", " ; ")
                .Replace("\r\n", " ; ")
                .Replace("\n", " ; ")
                .Replace("\"", "\\\"");
        }
        else
        {
            throw new NotSupportedException();
        }
    }
}
