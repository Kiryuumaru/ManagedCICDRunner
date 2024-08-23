using Application.Common;
using Application.LocalStore.Services;
using Application.Runner.Services;
using Application.Vagrant.Services;
using CliWrap.EventStream;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Runner.Models;
using Domain.Vagrant.Enums;
using Domain.Vagrant.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestfulHelpers;
using RestfulHelpers.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Application.Runner.Workers;

internal class RunnerWorker(ILogger<RunnerWorker> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private readonly ILogger<RunnerWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private readonly ExecutorLocker executorLocker = new();

    private readonly Dictionary<string, RunnerRuntime> runnerRuntimeMap = [];

    private readonly ConcurrentDictionary<string, RunnerInstance> buildingReplicaMap = [];
    private readonly ConcurrentDictionary<string, RunnerInstance> executingReplicaMap = [];

    private const string RunnerIdentifier = "managed_runner";
    private const string GithubRunnerVersion = "2.319.1";
    private const string GithubRunnerLinuxSHA256 = "3f6efb7488a183e291fc2c62876e14c9ee732864173734facc85a1bfb1744464";
    private const string GithubRunnerWindowsSHA256 = "1c78c51d20b817fb639e0b0ab564cf0469d083ad543ca3d0d7a2cdad5723f3a7";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromSeconds(5), false, stoppingToken, Routine, ex => _logger.LogError("Runner error: {msg}", ex.Message));
        RoutineExecutor.Execute(TimeSpan.FromSeconds(5), false, stoppingToken, Routine1, ex => _logger.LogError("Runner error: {msg}", ex.Message));
        return Task.CompletedTask;
    }

    private async Task Routine1(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var runnerService = scope.ServiceProvider.GetRequiredService<RunnerService>();
        var runnerTokenService = scope.ServiceProvider.GetRequiredService<RunnerTokenService>();
        var vagrantService = scope.ServiceProvider.GetRequiredService<VagrantService>();
        var localStore = scope.ServiceProvider.GetRequiredService<LocalStoreService>();
        var runnerRuntimeHolder = scope.ServiceProvider.GetSingletonObjectHolder<Dictionary<string, RunnerRuntime>>();

        await Task.Delay(1000, stoppingToken);
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var runnerService = scope.ServiceProvider.GetRequiredService<RunnerService>();
        var runnerTokenService = scope.ServiceProvider.GetRequiredService<RunnerTokenService>();
        var vagrantService = scope.ServiceProvider.GetRequiredService<VagrantService>();
        var localStore = scope.ServiceProvider.GetRequiredService<LocalStoreService>();
        var runnerRuntimeHolder = scope.ServiceProvider.GetSingletonObjectHolder<Dictionary<string, RunnerRuntime>>();

        _logger.LogDebug("Runner routine start...");
        string? runnerControllerId = (await localStore.Get<string>("runner_controller_id", cancellationToken: stoppingToken)).Value;
        if (string.IsNullOrEmpty(runnerControllerId))
        {
            _logger.LogDebug("Setting controller id");
            runnerControllerId = StringHelpers.Random(6, false).ToLowerInvariant();
            (await localStore.Set("runner_controller_id", runnerControllerId, cancellationToken: stoppingToken)).ThrowIfError();
        }

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "ManagedCICDRunner");

        _logger.LogDebug("Fetching runner token entities from service...");
        var runnerTokenEntityMap = (await runnerTokenService.GetAll(stoppingToken)).GetValueOrThrow();

        _logger.LogDebug("Fetching runner entities from service...");
        var runnerEntityMap = (await runnerService.GetAll(stoppingToken)).GetValueOrThrow();

        _logger.LogDebug("Fetching vagrant instances from service...");
        var vagrantReplicas = await vagrantService.GetReplicas(stoppingToken);

        _logger.LogDebug("Fetching runner token actions from API...");
        Dictionary<string, (RunnerTokenEntity RunnerTokenEntity, List<RunnerAction> RunnerActions)> runnerTokenMap = [];
        Dictionary<string, (RunnerAction RunnerAction, RunnerTokenEntity RunnerTokenEntity)> runnerActionMap = [];
        foreach (var runnerToken in runnerTokenEntityMap.Values)
        {
            var runnerListResult = await Execute<JsonDocument>(httpClient, HttpMethod.Get, runnerToken, "actions/runners", stoppingToken);
            if (runnerListResult.IsError)
            {
                string name = string.IsNullOrEmpty(runnerToken.GithubOrg) ? "" : $"{runnerToken.GithubOrg}/";
                name += string.IsNullOrEmpty(runnerToken.GithubRepo) ? "" : $"{runnerToken.GithubRepo}";
                name = name.Trim('/');
                throw new Exception($"Error fetching runners for runner token {name}: {runnerListResult.Error!.Message}");
            }
            List<RunnerAction> runnerActions = [];
            if (runnerListResult.HasValue)
            {
                foreach (var runnerJson in runnerListResult.Value.RootElement.GetProperty("runners").EnumerateArray())
                {
                    string name = runnerJson.GetProperty("name").GetString()!;
                    var nameSplit = name.Split('-');
                    if (nameSplit.Length == 5 && nameSplit[0] == "managed_runner" && nameSplit[1] == runnerControllerId)
                    {
                        string id = runnerJson.GetProperty("id").GetInt32().ToString();
                        string statusStr = runnerJson.GetProperty("status").GetString()!;
                        bool busy = runnerJson.GetProperty("busy").GetBoolean();
                        RunnerActionStatus status;
                        if (statusStr.Equals("online"))
                        {
                            status = busy ? RunnerActionStatus.Busy : RunnerActionStatus.Ready;
                        }
                        else
                        {
                            status = RunnerActionStatus.Offline;
                        }
                        var runnerId = nameSplit[2];
                        var runnerAction = new RunnerAction()
                        {
                            Id = id,
                            RunnerId = runnerId,
                            Name = name,
                            Status = status
                        };
                        runnerActions.Add(runnerAction);
                        runnerActionMap[name] = (runnerAction, runnerToken);
                    }
                }
            }
            runnerTokenMap[runnerToken.Id] = (runnerToken, runnerActions);
        }

        _logger.LogDebug("Updating entities to runtime runners map...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values.ToArray())
        {
            var runnerEntity = runnerEntityMap.GetValueOrDefault(runnerRuntime.RunnerId);
            var runnerTokenEntity = runnerTokenEntityMap.GetValueOrDefault(runnerRuntime.TokenId);
            runnerRuntimeMap[runnerRuntime.RunnerId] = new()
            {
                TokenId = runnerRuntime.TokenId,
                TokenRev = runnerRuntime.TokenRev,
                RunnerId = runnerRuntime.RunnerId,
                RunnerRev = runnerRuntime.RunnerRev,
                RunnerTokenEntity = runnerTokenEntity ?? runnerRuntime.RunnerTokenEntity,
                RunnerEntity = runnerEntity ?? runnerRuntime.RunnerEntity,
                Runners = runnerRuntime.Runners,
            };
            foreach (var runner in runnerRuntime.Runners.Values)
            {
                var vagrantReplica = vagrantReplicas.GetValueOrDefault(runner.Name);
                var (RunnerAction, _) = runnerActionMap.GetValueOrDefault(runner.Name);
                runnerRuntime.Runners[runner.Name] = new()
                {
                    Name = runner.Name,
                    VagrantReplica = vagrantReplica,
                    RunnerAction = RunnerAction,
                    Status = runner.Status,
                };
            }
        }

        _logger.LogDebug("Adding entities to runtime runners map...");
        foreach (var runnerEntity in runnerEntityMap.Values)
        {
            if (runnerRuntimeMap.ContainsKey(runnerEntity.Id))
            {
                continue;
            }
            if (!runnerTokenEntityMap.TryGetValue(runnerEntity.TokenId, out var runnerTokenEntity))
            {
                continue;
            }
            runnerRuntimeMap[runnerEntity.Id] = new()
            {
                TokenId = runnerTokenEntity.Id,
                TokenRev = runnerTokenEntity.Rev,
                RunnerId = runnerEntity.Id,
                RunnerRev = runnerEntity.Rev,
                RunnerTokenEntity = runnerTokenEntity,
                RunnerEntity = runnerEntity,
                Runners = [],
            };
        }

        _logger.LogDebug("Adding vagrant replicas to runtime runners...");
        foreach (var vagrantReplica in vagrantReplicas.Values)
        {
            if (vagrantReplica == null)
            {
                continue;
            }
            var runnerId = vagrantReplica.Labels["runnerId"];
            if (!runnerRuntimeMap.TryGetValue(runnerId, out var runnerRuntime))
            {
                continue;
            }
            var (RunnerAction, _) = runnerActionMap.GetValueOrDefault(vagrantReplica.Id);
            runnerRuntime.Runners[vagrantReplica.Id] = new()
            {
                Name = vagrantReplica.Id,
                VagrantReplica = vagrantReplica,
                RunnerAction = RunnerAction,
                Status = RunnerStatus.Building,
            };
        }

        _logger.LogDebug("Adding action runners to runtime runners...");
        foreach (var (RunnerAction, RunnerTokenEntity) in runnerActionMap.Values)
        {
            if (!runnerRuntimeMap.TryGetValue(RunnerAction.RunnerId, out var runnerRuntime))
            {
                continue;
            }
            if (runnerRuntime.Runners.ContainsKey(RunnerAction.Name))
            {
                continue;
            }
            var vagrantReplica = vagrantReplicas.GetValueOrDefault(RunnerAction.Name);
            runnerRuntime.Runners[RunnerAction.Name] = new()
            {
                Name = RunnerAction.Name,
                VagrantReplica = vagrantReplica,
                RunnerAction = RunnerAction,
                Status = RunnerStatus.Building,
            };
        }

        _logger.LogDebug("Removing deleted runners...");
        List<Task> runnersTokensDeleteTasks = [];
        foreach (var runnerTokenEntity in runnerTokenEntityMap.Values)
        {
            if (!runnerTokenEntity.Deleted)
            {
                continue;
            }
            runnersTokensDeleteTasks.Add(Task.Run(async () =>
            {
                List<Task> runnersTokenDeleteTasks = [];
                foreach (var runnerRuntime in runnerRuntimeMap.Values.Where(i => i.TokenId == runnerTokenEntity.Id))
                {
                    runnersTokenDeleteTasks.Add(Task.Run(async () =>
                    {
                        (await runnerService.Delete(runnerRuntime.RunnerEntity.Id, false, stoppingToken)).ThrowIfError();
                        List<Task> runnersDeleteTasks = [];
                        foreach (var runner in runnerRuntime.Runners.Values.ToArray())
                        {
                            runnersDeleteTasks.Add(Task.Run(async () =>
                            {
                                await DeleteRunner(runner, runnerTokenEntity, vagrantService, httpClient, stoppingToken);
                                runnerRuntime.Runners.Remove(runner.Name);
                                _logger.LogInformation("Runner purged (token deleted): {name}", runner.Name);
                            }, stoppingToken));
                        }
                        await Task.WhenAll(runnersDeleteTasks);
                        (await runnerService.Delete(runnerRuntime.RunnerId, true, stoppingToken)).ThrowIfError();
                        runnerRuntimeMap.Remove(runnerRuntime.RunnerId);
                        _logger.LogInformation("Runner instance purged (token deleted): {name}", runnerRuntime.RunnerId);
                    }, stoppingToken));
                }
                await Task.WhenAll(runnersTokenDeleteTasks);
                (await runnerTokenService.Delete(runnerTokenEntity.Id, true, stoppingToken)).ThrowIfError();
                _logger.LogInformation("Runner token purged (token deleted): {name}", runnerTokenEntity.Id);
            }, stoppingToken));
        }
        await Task.WhenAll(runnersTokensDeleteTasks);
        List<Task> runnersEntitiesDeleteTasks = [];
        foreach (var runnerEntity in runnerEntityMap.Values)
        {
            if (!runnerEntity.Deleted)
            {
                continue;
            }
            runnersEntitiesDeleteTasks.Add(Task.Run(async () =>
            {
                if (!runnerTokenEntityMap.TryGetValue(runnerEntity.TokenId, out var runnerTokenEntity))
                {
                    return;
                }
                if (!runnerRuntimeMap.TryGetValue(runnerEntity.Id, out var runnerRuntime))
                {
                    return;
                }
                List<Task> runnersEntityDeleteTasks = [];
                foreach (var runner in runnerRuntime.Runners.Values.ToArray())
                {
                    runnersEntityDeleteTasks.Add(Task.Run(async () =>
                    {
                        await DeleteRunner(runner, runnerTokenEntity, vagrantService, httpClient, stoppingToken);
                        runnerRuntime.Runners.Remove(runner.Name);
                        _logger.LogInformation("Runner purged (runner deleted): {name}", runner.Name);
                    }, stoppingToken));
                }
                await Task.WhenAll(runnersEntityDeleteTasks);
                (await runnerService.Delete(runnerEntity.Id, true, stoppingToken)).ThrowIfError();
                runnerRuntimeMap.Remove(runnerEntity.Id);
                _logger.LogInformation("Runner instance purged (runner deleted): {name}", runnerEntity.Id);
            }, stoppingToken));
        }
        await Task.WhenAll(runnersEntitiesDeleteTasks);

        _logger.LogDebug("Removing dangling runner actions...");
        Dictionary<string, VagrantReplica?> vagrantReplicaToRemove = [];
        Dictionary<string, (RunnerAction RunnerAction, RunnerTokenEntity RunnerTokenEntity)> runnerActionsToRemove = [];
        foreach (var vagrantReplicaPair in vagrantReplicas)
        {
            if (vagrantReplicaPair.Value == null)
            {
                vagrantReplicaToRemove[vagrantReplicaPair.Key] = null;
            }
            else
            {
                var (RunnerAction, RunnerTokenEntity) = runnerActionMap.GetValueOrDefault(vagrantReplicaPair.Value.Id);
                if (!buildingReplicaMap.ContainsKey(vagrantReplicaPair.Value.Id) &&
                    !executingReplicaMap.ContainsKey(vagrantReplicaPair.Value.Id) &&
                    (
                        vagrantReplicaPair.Value.State == VagrantReplicaState.Off ||
                        vagrantReplicaPair.Value.State == VagrantReplicaState.NotCreated ||
                        RunnerAction == null ||
                        RunnerAction.Status == RunnerActionStatus.Offline
                    ))
                {
                    vagrantReplicaToRemove[vagrantReplicaPair.Value.Id] = vagrantReplicaPair.Value;
                    if (RunnerAction != null)
                    {
                        runnerActionsToRemove[RunnerAction.Name] = (RunnerAction, RunnerTokenEntity);
                    }
                }
            }
        }
        foreach (var (RunnerAction, RunnerTokenEntity) in runnerActionMap.Values)
        {
            var vagrantReplica = vagrantReplicas.GetValueOrDefault(RunnerAction.Name);
            if (!buildingReplicaMap.ContainsKey(RunnerAction.Name) &&
                !executingReplicaMap.ContainsKey(RunnerAction.Name) &&
                (
                    RunnerAction.Status == RunnerActionStatus.Offline ||
                    vagrantReplica == null ||
                    vagrantReplica.State == VagrantReplicaState.Off ||
                    vagrantReplica.State == VagrantReplicaState.NotCreated)
                )
            {
                runnerActionsToRemove[RunnerAction.Name] = (RunnerAction, RunnerTokenEntity);
                if (vagrantReplica != null)
                {
                    vagrantReplicaToRemove[vagrantReplica.Id] = vagrantReplica;
                }
            }
        }
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            foreach (var runner in runnerRuntime.Runners.Values.ToArray())
            {
                if (vagrantReplicaToRemove.ContainsKey(runner.Name) ||
                    runnerActionsToRemove.ContainsKey(runner.Name) ||
                    (
                        !buildingReplicaMap.ContainsKey(runner.Name) &&
                        !executingReplicaMap.ContainsKey(runner.Name) &&
                        (
                            runner.RunnerAction == null ||
                            runner.RunnerAction.Status == RunnerActionStatus.Offline ||
                            runner.VagrantReplica == null ||
                            runner.VagrantReplica.State == VagrantReplicaState.Off ||
                            runner.VagrantReplica.State == VagrantReplicaState.NotCreated
                        )
                    ))
                {
                    if (runner.RunnerAction != null)
                    {
                        runnerActionsToRemove[runner.RunnerAction.Name] = (runner.RunnerAction, runnerRuntime.RunnerTokenEntity);
                    }
                    if (runner.VagrantReplica != null)
                    {
                        vagrantReplicaToRemove[runner.VagrantReplica.Id] = runner.VagrantReplica;
                    }
                    runnerRuntime.Runners.Remove(runner.Name);
                }
            }
        }
        List<Task> deleteDanglingTasks = [];
        foreach (var vagrantReplicaPair in vagrantReplicaToRemove)
        {
            deleteDanglingTasks.Add(Task.Run(async () =>
            {
                var id = vagrantReplicaPair.Value?.Id ?? vagrantReplicaPair.Key;
                await DeleteRunnerVagrantReplica(id, vagrantService, stoppingToken);
                _logger.LogInformation("Vagrant replica purged (dead replica): {name}", id);

            }, stoppingToken));
        }
        foreach (var (RunnerAction, RunnerTokenEntity) in runnerActionsToRemove.Values)
        {
            deleteDanglingTasks.Add(Task.Run(async () =>
            {
                await DeleteRunnerAction(RunnerAction, RunnerTokenEntity, httpClient, stoppingToken);
                _logger.LogInformation("Runner action purged (dead action): {name}", RunnerAction.Name);
            }, stoppingToken));
        }
        await Task.WhenAll(deleteDanglingTasks);

        _logger.LogDebug("Removing outdated runners...");
        List<Task> deleteAllOutdatedTasks = [];
        foreach (var runnerRuntime in runnerRuntimeMap.Values.ToArray())
        {
            if (runnerRuntime.TokenRev == runnerRuntime.RunnerTokenEntity.Rev &&
                runnerRuntime.RunnerRev == runnerRuntime.RunnerEntity.Rev)
            {
                continue;
            }
            deleteAllOutdatedTasks.Add(Task.Run(async () =>
            {
                List<Task> deleteOutdatedTasks = [];
                foreach (var runner in runnerRuntime.Runners.Values.ToArray())
                {
                    deleteOutdatedTasks.Add(Task.Run(async () =>
                    {
                        await DeleteRunner(runner, runnerRuntime.RunnerTokenEntity, vagrantService, httpClient, stoppingToken);
                        runnerRuntime.Runners.Remove(runner.Name);
                        _logger.LogInformation("Runner purged (outdated): {name}", runner.Name);
                    }, stoppingToken));
                }
                await Task.WhenAll(deleteOutdatedTasks);
                runnerRuntimeMap[runnerRuntime.RunnerId] = new()
                {
                    TokenId = runnerRuntime.TokenId,
                    TokenRev = runnerRuntime.RunnerTokenEntity.Rev,
                    RunnerId = runnerRuntime.RunnerId,
                    RunnerRev = runnerRuntime.RunnerEntity.Rev,
                    RunnerTokenEntity = runnerRuntime.RunnerTokenEntity,
                    RunnerEntity = runnerRuntime.RunnerEntity,
                    Runners = runnerRuntime.Runners,
                };
            }, stoppingToken));
        }
        await Task.WhenAll(deleteAllOutdatedTasks);

        _logger.LogDebug("Removing excess runners...");
        List<Task> deleteAllExcessTasks = [];
        foreach (var runnerRuntime in runnerRuntimeMap.Values.ToArray())
        {
            int numExcess = runnerRuntime.Runners.Count - runnerRuntime.RunnerEntity.Replicas;
            if (numExcess > 0)
            {
                List<RunnerInstance> runnerInstancesToRemove = [];
                while (numExcess >= runnerInstancesToRemove.Count)
                {
                    var runner = runnerRuntime.Runners.Values.FirstOrDefault(i => i.Status == RunnerStatus.Ready && !runnerInstancesToRemove.Contains(i)) ??
                        runnerRuntime.Runners.Values.FirstOrDefault(i => !runnerInstancesToRemove.Contains(i));
                    if (runner == null)
                    {
                        break;
                    }
                    runnerInstancesToRemove.Add(runner);
                }
                foreach (var runner in runnerInstancesToRemove)
                {
                    deleteAllExcessTasks.Add(Task.Run(async () =>
                    {
                        await DeleteRunner(runner, runnerRuntime.RunnerTokenEntity, vagrantService, httpClient, stoppingToken);
                        runnerRuntime.Runners.Remove(runner.Name);
                        _logger.LogInformation("Runner purged (excess): {name}", runner.Name);
                    }, stoppingToken));
                }
            }
        }
        await Task.WhenAll(deleteAllExcessTasks);

        _logger.LogDebug("Removing dangling builds...");
        List<Task> deleteDanglingBuildsTasks = [];
        foreach (var vagrantBuildPair in await vagrantService.GetBuilds(stoppingToken))
        {
            deleteDanglingBuildsTasks.Add(Task.Run(async () =>
            {
                string id = vagrantBuildPair.Value?.Id ?? vagrantBuildPair.Key;

                string[] idSplit = id.Split('-');
                if (idSplit.Length < 3 ||
                    idSplit[0] == RunnerIdentifier ||
                    idSplit[1] == runnerControllerId ||
                    runnerRuntimeMap.ContainsKey(idSplit[2]))
                {
                    return;
                }

                await vagrantService.DeleteBuild(id, stoppingToken);
                _logger.LogInformation("Runner vagrant build (dangling): {name}", id);

            }, stoppingToken));
        }
        await Task.WhenAll(deleteDanglingBuildsTasks);

        _logger.LogDebug("Updating runtime runner instance status...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            foreach (var runner in runnerRuntime.Runners.Values.ToArray())
            {
                RunnerStatus runnerStatus = RunnerStatus.Building;
                if (runner.VagrantReplica != null && runner.RunnerAction != null)
                {
                    runnerStatus = runner.RunnerAction.Status switch
                    {
                        RunnerActionStatus.Busy => RunnerStatus.Busy,
                        RunnerActionStatus.Ready => RunnerStatus.Ready,
                        _ => RunnerStatus.Starting
                    };
                }
                else if (runner.VagrantReplica != null && runner.RunnerAction == null)
                {
                    runnerStatus = RunnerStatus.Starting;
                }
                runnerRuntime.Runners[runner.Name] = new()
                {
                    Name = runner.Name,
                    VagrantReplica = runner.VagrantReplica,
                    RunnerAction = runner.RunnerAction,
                    Status = runnerStatus
                };
            }
        }

        _logger.LogDebug("Checking for runners to upscale...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            if (!runnerTokenEntityMap.TryGetValue(runnerRuntime.TokenId, out var runnerToken))
            {
                continue;
            }
            if (runnerRuntime.RunnerEntity.Replicas > runnerRuntime.Runners.Count)
            {
                var runners = runnerRuntime.Runners.ToArray();
                var rev = $"{runnerRuntime.TokenRev}-{runnerRuntime.RunnerRev}";

                for (int i = 0; i < (runnerRuntime.RunnerEntity.Replicas - runners.Length); i++)
                {
                    string id = $"{RunnerIdentifier}-{runnerControllerId}-{runnerRuntime.RunnerEntity.Id.ToLowerInvariant()}";
                    string replicaId = $"{id}-{runnerRuntime.RunnerRev.ToLowerInvariant()}-{StringHelpers.Random(6, false).ToLowerInvariant()}";
                    string baseVagrantfile = await GetPath(runnerRuntime.RunnerEntity.Vagrantfile).ReadAllTextAsync(stoppingToken);
                    string baseVagrantBuildId = $"{id}-base";
                    string vagrantBuildId = $"{id}";
                    string runnerId = runnerRuntime.RunnerEntity.Id;
                    int cpus = runnerRuntime.RunnerEntity.Cpus;
                    int memoryGB = runnerRuntime.RunnerEntity.MemoryGB;
                    RunnerOSType runnerOs = runnerRuntime.RunnerEntity.RunnerOS;
                    string runnerOsStr = runnerOs switch
                    {
                        RunnerOSType.Linux => "linux",
                        RunnerOSType.Windows => "windows",
                        _ => throw new NotSupportedException()
                    };

                    runnerRuntime.Runners[replicaId] = new RunnerInstance()
                    {
                        Name = replicaId,
                        VagrantReplica = null,
                        RunnerAction = null,
                        Status = RunnerStatus.Building
                    };

                    _logger.LogInformation("Runner preparing: {id}", replicaId);

                    string bootstrapInputScript;
                    if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Linux)
                    {
                        bootstrapInputScript = $$"""
                            mkdir "/r"
                            cd "/r"                
                            RUNNER_VERSION={{GithubRunnerVersion}}
                            curl -fSL --output /tmp/actions-runner-linux-x64.tar.gz https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-linux-x64-${RUNNER_VERSION}.tar.gz
                            RUNNER_SHA256='{{GithubRunnerLinuxSHA256}}'
                            echo "$RUNNER_SHA256 /tmp/actions-runner-linux-x64.tar.gz" | sha256sum -c -
                            tar -xzf /tmp/actions-runner-linux-x64.tar.gz -C /r
                            ./bin/installdependencies.sh
                            """;
                    }
                    else if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Windows)
                    {
                        bootstrapInputScript = $$"""
                            $ErrorActionPreference='Stop'; $verbosePreference='Continue'; $ProgressPreference = "SilentlyContinue"
                            mkdir "C:\\r"
                            cd "C:\\r"
                            $RUNNER_VERSION = "{{GithubRunnerVersion}}"
                            Invoke-WebRequest "https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/actions-runner-win-x64-${RUNNER_VERSION}.zip" -OutFile "${env:TEMP}\\actions-runner-win-x64.zip" -UseBasicParsing;
                            $RUNNER_SHA256 = '{{GithubRunnerWindowsSHA256}}';
                            if ((Get-FileHash "${env:TEMP}\\actions-runner-win-x64.zip" -Algorithm sha256).Hash -ne $RUNNER_SHA256) {
                              Write-Host 'RUNNER_SHA256 CHECKSUM VERIFICATION FAILED!';
                              exit 1;
                            };
                            Expand-Archive "${env:TEMP}\\actions-runner-win-x64.zip" -DestinationPath c:\\r -Force;
                            """;
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }

                    Dictionary<string, string> labels = [];
                    labels["baseVagrantBuildId"] = baseVagrantBuildId;
                    labels["vagrantBuildId"] = vagrantBuildId;
                    labels["runnerId"] = runnerId;
                    labels["tokenRev"] = runnerRuntime.TokenRev.ToLowerInvariant();
                    labels["runnerRev"] = runnerRuntime.RunnerRev.ToLowerInvariant();

                    async Task<string> runnerInputScriptFactory()
                    {
                        var tokenResponse = await Execute<Dictionary<string, string>>(httpClient, HttpMethod.Post, runnerToken, "actions/runners/registration-token", stoppingToken);
                        tokenResponse.ThrowIfErrorOrHasNoValue();
                        string regToken = tokenResponse.Value["token"];
                        string inputArgs = $"--name {replicaId} --url {GetConfigUrl(runnerToken)} --work w --disableupdate --ephemeral --unattended --token {regToken}";
                        if (runnerRuntime.RunnerEntity.Labels.Length != 0)
                        {
                            inputArgs += $" --no-default-labels --labels {string.Join(',', runnerRuntime.RunnerEntity.Labels)}";
                        }
                        if (!string.IsNullOrEmpty(runnerRuntime.RunnerEntity.Group))
                        {
                            inputArgs += $" --runnergroup {runnerRuntime.RunnerEntity.Group}";
                        }
                        string inputScript;
                        if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Linux)
                        {
                            inputScript = NormalizeScriptInput(runnerRuntime.RunnerEntity.RunnerOS, $"""
                                export RUNNER_ALLOW_RUNASROOT=1
                                cd "/r"
                                sudo -E ./config.sh {inputArgs}
                                sudo -E ./run.sh
                                sudo shutdown -h now
                                """);
                        }
                        else if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Windows)
                        {
                            inputScript = NormalizeScriptInput(runnerRuntime.RunnerEntity.RunnerOS, $"""
                                $ErrorActionPreference="Stop"; $verbosePreference="Continue"; $ProgressPreference = "SilentlyContinue"
                                $env:RUNNER_ALLOW_RUNASROOT=1
                                cd "C:\r"
                                ./config.cmd {inputArgs}
                                ./run.cmd
                                shutdown /s /f
                                """);
                        }
                        else
                        {
                            throw new NotSupportedException();
                        }
                        return inputScript;
                    }

                    try
                    {
                        async void run()
                        {
                            try
                            {
                                buildingReplicaMap[replicaId] = runnerRuntime.Runners[replicaId];
                                await executorLocker.Execute(vagrantBuildId, async () =>
                                {
                                    string baseRev;
                                    try
                                    {
                                        var baseBuild = await vagrantService.Build(baseVagrantBuildId, "base", baseVagrantfile, stoppingToken);
                                        baseRev = $"{baseBuild.VagrantFileHash}-base_hash";
                                    }
                                    catch
                                    {
                                        try
                                        {
                                            await vagrantService.DeleteBuild(baseVagrantBuildId, stoppingToken);
                                        }
                                        catch { }
                                        throw;
                                    }
                                    try
                                    {
                                        await vagrantService.Build(runnerOs, baseVagrantBuildId, vagrantBuildId, baseRev, bootstrapInputScript, stoppingToken);
                                    }
                                    catch
                                    {
                                        try
                                        {
                                            await vagrantService.DeleteBuild(vagrantBuildId, stoppingToken);
                                        }
                                        catch { }
                                        throw;
                                    }

                                    executingReplicaMap[replicaId] = runnerRuntime.Runners[replicaId];
                                    async void execute()
                                    {
                                        try
                                        {
                                            try
                                            {
                                                _logger.LogInformation("Runner starting OS: {id}", replicaId);

                                                await vagrantService.Run(runnerOs, vagrantBuildId, replicaId, rev, cpus, memoryGB, labels, stoppingToken);
                                            }
                                            catch (Exception ex)
                                            {
                                                throw new Exception($"Runner rev run error on {replicaId}: {ex.Message}");
                                            }

                                            _logger.LogInformation("Runner created (up): {id}", replicaId);

                                            try
                                            {
                                                await vagrantService.Execute(runnerOs, replicaId, runnerInputScriptFactory, stoppingToken);
                                            }
                                            catch (Exception ex)
                                            {
                                                throw new Exception($"Runner rev execute error on {replicaId}: {ex.Message}");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError("{ex}", ex.Message);
                                        }
                                        finally
                                        {
                                            executingReplicaMap.Remove(replicaId, out _);
                                        }
                                    }
                                    execute();
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError("Runner rev build error on {}: {ex}", replicaId, ex.Message);
                                runnerRuntime.Runners.Remove(replicaId);
                            }
                            finally
                            {
                                buildingReplicaMap.Remove(replicaId, out _);
                            }
                        }
                        run();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Runner rev init error on {}: {ex}", replicaId, ex.Message);
                        runnerRuntime.Runners.Remove(replicaId);
                    }
                }
            }
        }

        await runnerRuntimeHolder.Set(() => new(runnerRuntimeMap));

        _logger.LogDebug("Runner routine end");
    }

    private static async Task<HttpResult> Execute(HttpClient httpClient, HttpMethod httpMethod, RunnerTokenEntity runnerTokenEntity, string segement, CancellationToken cancellationToken)
    {
        HttpRequestMessage requestMessage = new(httpMethod, GetEndpoint(runnerTokenEntity, segement));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerTokenEntity.GithubToken);
        return await httpClient.Execute(requestMessage, cancellationToken: cancellationToken);
    }

    private static async Task<HttpResult<T>> Execute<T>(HttpClient httpClient, HttpMethod httpMethod, RunnerTokenEntity runnerTokenEntity, string segement, CancellationToken cancellationToken)
    {
        HttpRequestMessage requestMessage = new(httpMethod, GetEndpoint(runnerTokenEntity, segement));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerTokenEntity.GithubToken);
        return await httpClient.Execute<T>(requestMessage, cancellationToken: cancellationToken);
    }

    private static string GetEndpoint(RunnerTokenEntity runnerTokenEntity, string segement)
    {
        if (!string.IsNullOrEmpty(runnerTokenEntity.GithubOrg) && !string.IsNullOrEmpty(runnerTokenEntity.GithubRepo))
        {
            return $"https://api.github.com/repos/{runnerTokenEntity.GithubOrg}/{runnerTokenEntity.GithubRepo}/{segement}";
        }
        else if (!string.IsNullOrEmpty(runnerTokenEntity.GithubOrg))
        {
            return $"https://api.github.com/orgs/{runnerTokenEntity.GithubOrg}/{segement}";
        }
        else
        {
            throw new Exception("GithubOrg and GithubRepo is empty");
        }
    }

    private static string GetConfigUrl(RunnerTokenEntity runnerTokenEntity)
    {
        if (!string.IsNullOrEmpty(runnerTokenEntity.GithubOrg) && !string.IsNullOrEmpty(runnerTokenEntity.GithubRepo))
        {
            return $"https://github.com/{runnerTokenEntity.GithubOrg}/{runnerTokenEntity.GithubRepo}";
        }
        else if (!string.IsNullOrEmpty(runnerTokenEntity.GithubOrg))
        {
            return $"https://github.com/{runnerTokenEntity.GithubOrg}";
        }
        else
        {
            throw new Exception("GithubOrg and GithubRepo is empty");
        }
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

    private static AbsolutePath GetPath(string path)
    {
        AbsolutePath absolutePath;

        if (Path.IsPathRooted(path))
        {
            absolutePath = path;
        }
        else
        {
            absolutePath = AbsolutePath.Create(Environment.CurrentDirectory) / path;
        }

        if (!absolutePath.FileExists())
        {
            throw new Exception($"\"{path}\" does not exists");
        }

        return absolutePath;
    }

    private async Task DeleteRunner(RunnerInstance runner, RunnerTokenEntity runnerTokenEntity, VagrantService vagrantService, HttpClient httpClient, CancellationToken cancellationToken)
    {
        List<Task> runnerDeleteTasks = [];
        runnerDeleteTasks.Add(DeleteRunnerAction(runner.RunnerAction, runnerTokenEntity, httpClient, cancellationToken));
        runnerDeleteTasks.Add(DeleteRunnerVagrantReplica(runner.VagrantReplica?.Id, vagrantService, cancellationToken));
        await Task.WhenAll(runnerDeleteTasks);
    }

    private async Task DeleteRunnerAction(RunnerAction? runnerAction, RunnerTokenEntity runnerTokenEntity, HttpClient httpClient, CancellationToken cancellationToken)
    {
        if (runnerAction != null)
        {
            try
            {
                (await Execute(httpClient, HttpMethod.Delete, runnerTokenEntity, $"actions/runners/{runnerAction.Id}", cancellationToken)).ThrowIfError();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Action runner not deleted ({name}): {err}", runnerAction.Name, ex.Message);
            }
        }
    }

    private async Task DeleteRunnerVagrantReplica(string? vagrantReplicaId, VagrantService vagrantService, CancellationToken cancellationToken)
    {
        if (vagrantReplicaId != null)
        {
            await vagrantService.DeleteReplica(vagrantReplicaId, cancellationToken);
        }
    }
}
