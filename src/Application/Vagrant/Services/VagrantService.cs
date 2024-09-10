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
        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Service"] = nameof(VagrantService),
            ["VagrantAction"] = nameof(VerifyClient)
        });

        await BuildPath.KillExceptHyperv(cancellationToken);
        await ReplicaPath.KillExceptHyperv(cancellationToken);
        await TempPath.KillExceptHyperv(cancellationToken);
        await VagrantHomePath.KillExceptHyperv(cancellationToken);

        await TempPath.Delete(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Checking vagrant version...");
            try
            {
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["version"], environmentVariables: VagrantEnvVars, stoppingToken: cancellationToken);
                break;
            }
            catch
            {
                _logger.LogDebug("Vagrant is not installed");
            }

            try
            {
                _logger.LogDebug("Downloading vagrant client installer...");

                using var httpClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();

                var clientInstallerDownloadPath = TempPath / "vagrant_windows_amd64.msi";

                await ClientPath.Delete(cancellationToken);
                await clientInstallerDownloadPath.Delete(cancellationToken);

                ClientPath.CreateDirectory();
                clientInstallerDownloadPath.Parent.CreateDirectory();

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

                _logger.LogDebug("Installing vagrant client done");
            }
            catch (Exception ex)
            {
                _logger.LogError("Error installing vagrant client: {err}", ex.Message);

                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.LogDebug("Patching ssh permissions...");
        await WindowsOSHelpers.TakeOwnPermission(VagrantHomePath / "insecure_private_key", cancellationToken);
        foreach (var keyPath in (VagrantHomePath / "insecure_private_keys").GetFiles())
        {
            await WindowsOSHelpers.TakeOwnPermission(keyPath, cancellationToken);
        }
    }

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
        if (buildFilePath.FileExists() && await buildFilePath.Read<JsonDocument>(cancellationToken: cancellationToken) is JsonDocument buildJson &&
            buildJson.RootElement.TryGetProperty("vagrantFileHash", out var vagrantFileHashProp) &&
            buildJson.RootElement.TryGetProperty("rev", out var revProp) &&
            vagrantFileHashProp.ValueKind == JsonValueKind.String &&
            revProp.ValueKind == JsonValueKind.String)
        {
            currentVagrantFileHash = vagrantFileHashProp.GetString()!;
            currentRev = revProp.GetString()!;
        }

        string vagrantfileContent = await vagrantfileFactory(boxPath);

        await vagrantfilePathTemp.WriteAllText(vagrantfileContent, cancellationToken);
        vagrantFileHash = await vagrantfilePathTemp.GetHashSHA512(cancellationToken);
        await boxPathTemp.WaitKillAll([], cancellationToken.WithTimeout(TimeSpan.FromMinutes(2)));

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

            await vagrantfilePath.WriteAllText(vagrantfileContent, cancellationToken);

            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(Build),
                ["VagrantBuildId"] = buildId,
                ["VagrantBuildRev"] = rev,
                ["VagrantFileHash"] = vagrantFileHash
            });

            try
            {
                _logger.LogDebug("Building vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["up", "--provider", "hyperv"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                _logger.LogDebug("Reloading vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["reload"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                _logger.LogDebug("Packaging vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["package", "--output", packageBoxPath], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                _logger.LogDebug("Saving vagrant build {VagrantBuildId}", buildId);
                await Cli.RunListenAndLog(_logger, ClientExecPath, ["box", "add", packageBoxPath, "--name", buildId, "-f"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);

                _logger.LogDebug("Finalizing ssh permissions {VagrantBuildId}", buildId);
                await WindowsOSHelpers.TakeOwnPermission(VagrantHomePath / "boxes" / buildId / "0" / "hyperv" / "vagrant_private_key", cancellationToken);

                _logger.LogDebug("Cleaning vagrant build {VagrantBuildId}", buildId);
                await DeleteVMCore(boxPath, buildId, cancellationToken);

                var buildObj = new
                {
                    id = buildId,
                    vagrantFileHash,
                    rev
                };
                await buildFilePath.Write(buildObj, cancellationToken: cancellationToken);

                _logger.LogDebug("Vagrant build was built {VagrantBuildId}", buildId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on building vagrant build {VagrantBuildId}: {Error}", buildId, ex);
                throw;
            }
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
                  config.ssh.insert_key = true
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

        if (!vagrantfilePath.FileExists() || !packageBoxPath.FileExists() || !buildFilePath.FileExists() || await buildFilePath.Read<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument buildJson ||
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
            if (await buildFilePath.Read<JsonDocument>(cancellationToken: cancellationToken) is JsonDocument buildJson)
            {
                buildId = buildJson.RootElement.GetProperty("id").GetString()!;
            }
        }
        catch { }

        await locker.Execute(buildId, async () =>
        {
            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(DeleteBuild),
                ["VagrantBuildId"] = buildId
            });

            _logger.LogDebug("Deleting vagrant build {VagrantBuildId}", buildId);

            try
            {
                await DeleteCore(boxPath, $"{id}_default_", cancellationToken);
            }
            catch { }
            try
            {
                await Cli.RunOnce(ClientExecPath, ["box", "remove", buildId, "-f"], boxPath, VagrantEnvVars, stoppingToken: cancellationToken);
            }
            catch { }

            _logger.LogDebug("Deleted vagrant build {VagrantBuildId}", buildId);
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

        bool isLocked = false;
        var runTask = Task.Run(async () => {

            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(Run),
                ["VagrantBuildId"] = buildId,
                ["VagrantReplicaId"] = replicaId,
                ["VagrantReplicaRev"] = rev,
            });

            try
            {
                _logger.LogDebug("Running vagrant replica {VagrantReplicaId}", replicaId);

                while (!isLocked)
                {
                    await Task.Delay(500, cancellationToken);
                }

                var replicaObj = new
                {
                    buildId,
                    replicaId,
                    rev,
                    labels
                };

                await replicaFilePath.Write(replicaObj, cancellationToken: cancellationToken);
                await vagrantfilePath.WriteAllText(await vagrantfileFactory(replicaPath), cancellationToken: cancellationToken);

                await foreach (var cmdEvent in Cli.RunListen(ClientExecPath, ["up", "--provider", "hyperv"], replicaPath, VagrantEnvVars, stoppingToken: cancellationToken))
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

                _logger.LogDebug("Vagrant replica {VagrantReplicaId} is running", replicaId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on running vagrant replica {VagrantReplicaId}: {Error}", replicaId, ex);
                throw;
            }
        }, cancellationToken);

        await locker.Execute([buildId, replicaId], async () =>
        {
            isLocked = true;
            while (await GetStateCore(replicaPath, cancellationToken) != VagrantReplicaState.Running && !runTask.IsCompleted)
            {
                await Task.Delay(1000, cancellationToken);
            }
        });

        await runTask;
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
                  config.ssh.insert_key = true
                  config.vm.provider "hyperv" do |hv|
                    hv.enable_virtualization_extensions = true
                    hv.linked_clone = true
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

        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() || await replicaFilePath.Read<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument replicaJson ||
            !replicaJson.RootElement.TryGetProperty("buildId", out var buildIdProp) ||
            !replicaJson.RootElement.TryGetProperty("replicaId", out var replicaIdProp) ||
            !replicaJson.RootElement.TryGetProperty("rev", out var revProp) ||
            !replicaJson.RootElement.TryGetProperty("labels", out var labelsProp) ||
            buildIdProp.ValueKind != JsonValueKind.String ||
            replicaIdProp.ValueKind != JsonValueKind.String ||
            revProp.ValueKind != JsonValueKind.String ||
            labelsProp.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string buildId = buildIdProp.GetString()!;
        string replicaRev = revProp.GetString()!;

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

        bool isLocked = false;
        var executeTask = Task.Run(async () =>
        {
            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(Execute),
                ["VagrantBuildId"] = buildId,
                ["VagrantReplicaId"] = replicaId,
                ["VagrantReplicaRev"] = replicaRev
            });

            try
            {
                _logger.LogDebug("Executing a script on vagrant replica {VagrantReplicaId}", replicaId);
                
                while (!isLocked)
                {
                    await Task.Delay(500, cancellationToken);
                }

                CancellationTokenSource ctx = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var ct = ctx.Token;
                List<Task> tasks = [];

                tasks.Add(Task.Run(async () =>
                {
                    await foreach (var cmdEvent in Cli.RunListen(ClientExecPath, [vmCommunicator, "-c", "\"" + await inputScriptFactory() + "\""], replicaPath, VagrantEnvVars, stoppingToken: ct))
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
                    if (!ct.IsCancellationRequested)
                    {
                        ctx.Cancel();
                    }

                }, ct));

                tasks.Add(Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && await GetStateCore(replicaPath, cancellationToken) == VagrantReplicaState.Running)
                    {
                        await Task.Delay(2000);
                    }
                    if (!ct.IsCancellationRequested)
                    {
                        ctx.Cancel();
                    }
                }, ct));

                await Task.WhenAny(tasks);

                _logger.LogDebug("Script execution done on vagrant replica {VagrantReplicaId}", replicaId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on executing a script on vagrant replica {VagrantReplicaId}: {Error}", replicaId, ex);
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

    public async Task<VagrantReplica?> GetReplica(string id, CancellationToken cancellationToken)
    {
        AbsolutePath dir = ReplicaPath / id;
        AbsolutePath vagrantfilePath = dir / "Vagrantfile";
        AbsolutePath replicaFilePath = dir / "replica.json";

        if (!vagrantfilePath.FileExists() || !replicaFilePath.FileExists() || await replicaFilePath.Read<JsonDocument>(cancellationToken: cancellationToken) is not JsonDocument replicaJson ||
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

        VagrantReplicaState vagrantReplicaState = await GetStateCore(dir, cancellationToken);

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
            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["Service"] = nameof(VagrantService),
                ["VagrantAction"] = nameof(DeleteReplica),
                ["VagrantReplicaId"] = id
            });

            _logger.LogDebug("Deleting vagrant replica {VagrantReplicaId}", id);

            try
            {
                await DeleteCore(dir, $"{id}_default_", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on deleting a vagrant replica {VagrantReplicaId}: {Error}", id, ex);
                throw;
            }

            _logger.LogDebug("Deleted vagrant replica {VagrantReplicaId}", id);
        });
    }

    public async Task DeleteCore(AbsolutePath dir, string id, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!dir.DirectoryExists())
                {
                    break;
                }

                await DeleteVMCore(dir, id, cancellationToken.WithTimeout(TimeSpan.FromMinutes(2)));

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
                _logger.LogWarning("Error on deleting {}: {}. retrying...", id, ex.Message);
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
                var ctxTimed = cancellationToken.WithTimeout(TimeSpan.FromSeconds(30));
                string? vmId = null;
                try
                {
                    var rawGetVm = await Cli.RunOnce("powershell", ["Get-VM | ConvertTo-Json"], environmentVariables: VagrantEnvVars, stoppingToken: ctxTimed);
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
                        var vmName = prop!.GetProperty("Name").GetString()!;
                        if (prop!.GetProperty("Name").GetString()!.StartsWith(id, StringComparison.InvariantCultureIgnoreCase))
                        {
                            vmId = vmName;
                            break;
                        }
                    }
                }
                catch { }
                if (vmId != null)
                {
                    try
                    {
                        await Cli.RunOnce("powershell", ["Stop-VM", "-Name", vmId, "-TurnOff", "-Force"], environmentVariables: VagrantEnvVars, stoppingToken: ctxTimed);
                    }
                    catch { }
                    while (!ctxTimed.IsCancellationRequested)
                    {
                        try
                        {
                            var result = await Cli.RunOnce("powershell", [$"(Get-VM -Name \"{vmId}\").State"], environmentVariables: VagrantEnvVars, stoppingToken: ctxTimed);
                            if (result.Trim().Equals("off", StringComparison.InvariantCultureIgnoreCase))
                            {
                                break;
                            }
                        }
                        catch
                        {
                            break;
                        }
                    }
                    try
                    {
                        await Cli.RunOnce("powershell", ["Remove-VM", "-Name", vmId, "-Force"], environmentVariables: VagrantEnvVars, stoppingToken: ctxTimed);
                    }
                    catch { }
                    while (!ctxTimed.IsCancellationRequested)
                    {
                        try
                        {
                            await Cli.RunOnce("powershell", ["Get-VM", "-Name", vmId], environmentVariables: VagrantEnvVars, stoppingToken: ctxTimed);
                        }
                        catch
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
                _logger.LogDebug("Error on deleting VM {}: {}. retrying...", id, ex.Message);
                _logger.LogWarning("Error on deleting VM {}. retrying...", id);
            }
        }
    }

    private async Task<VagrantReplicaState> GetStateCore(AbsolutePath vagrantDir, CancellationToken cancellationToken)
    {
        VagrantReplicaState vagrantReplicaState = VagrantReplicaState.NotCreated;

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
                var vmName = prop!.GetProperty("Name").GetString()!;
                if (prop!.GetProperty("Name").GetString()!.StartsWith($"{vagrantDir.Name}_default_", StringComparison.InvariantCultureIgnoreCase))
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
                    break;
                }
            }
        }
        catch { }

        return vagrantReplicaState;
    }
}
