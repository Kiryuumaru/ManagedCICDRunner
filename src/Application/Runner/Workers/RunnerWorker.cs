using Application.Common;
using Application.Docker.Services;
using Application.LocalStore.Services;
using Application.Runner.Services;
using Domain.Docker.Enums;
using Domain.Docker.Models;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Runner.Models;
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
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Application.Runner.Workers;

internal class RunnerWorker(ILogger<RunnerWorker> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private readonly ILogger<RunnerWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private readonly static AbsolutePath HostAssetsDir = AbsolutePath.Parse(Environment.CurrentDirectory) / "HostAssets";
    private readonly static AbsolutePath LinuxHostAssetsDir = HostAssetsDir / "linux";
    private readonly static AbsolutePath WindowsHostAssetsDir = HostAssetsDir / "windows";
    private readonly static AbsolutePath LinuxActionsRunner = LinuxHostAssetsDir / "actions-runner-linux-x64.tar.gz";
    private readonly static AbsolutePath WindowsActionsRunner = WindowsHostAssetsDir / "actions-runner-win-x64.zip";
    private readonly static AbsolutePath ActionsRunnerDockerfilesDir = AbsolutePath.Parse(Environment.CurrentDirectory) / "Dockerfiles" / ".ActionsRunner";

    private readonly static (string OS, string Url, string Hash, AbsolutePath Path)[] HostAssetMatrix =
    [
        (
            "linux",
            "https://github.com/actions/runner/releases/download/v2.317.0/actions-runner-linux-x64-2.317.0.tar.gz",
            "9e883d210df8c6028aff475475a457d380353f9d01877d51cc01a17b2a91161d",
            LinuxActionsRunner
        ), (
            "windows",
            "https://github.com/actions/runner/releases/download/v2.317.0/actions-runner-win-x64-2.317.0.zip",
            "a74dcd1612476eaf4b11c15b3db5a43a4f459c1d3c1807f8148aeb9530d69826",
            WindowsActionsRunner
        )
    ];

    private readonly ExecutorLocker executorLocker = new();

    private readonly Dictionary<string, RunnerRuntime> runnerRuntimeMap = [];

    private readonly ConcurrentDictionary<string, RunnerInstance> building = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromSeconds(5), stoppingToken, Routine, ex => _logger.LogError("Runner error: {msg}", ex.Message));
        return Task.CompletedTask;
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var runnerService = scope.ServiceProvider.GetRequiredService<RunnerService>();
        var runnerTokenService = scope.ServiceProvider.GetRequiredService<RunnerTokenService>();
        var dockerService = scope.ServiceProvider.GetRequiredService<DockerService>();
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

        _logger.LogDebug("Fetching common assets...");
        List<Task> commonAssetsTasks = [];
        foreach (var (os, url, hash, actionRunnersPath) in HostAssetMatrix)
        {
            commonAssetsTasks.Add(Task.Run(async () =>
            {
                while (!actionRunnersPath.FileExists() || !FileHasher.GetFileHash(actionRunnersPath).Equals(hash, StringComparison.InvariantCultureIgnoreCase))
                {
                    _logger.LogInformation("Downloading common asset {}...", url);
                    try
                    {
                        actionRunnersPath.Parent.CreateDirectory();
                        if (actionRunnersPath.FileExists())
                        {
                            actionRunnersPath.DeleteFile();
                        }
                        using var s = await httpClient.GetStreamAsync(url, stoppingToken);
                        using var fs = new FileStream(actionRunnersPath, FileMode.OpenOrCreate);
                        await s.CopyToAsync(fs, stoppingToken);
                        _logger.LogInformation("Downloading common asset {} done", url);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Downloading common asset {} error: {}", url, ex);
                        await Task.Delay(2000, stoppingToken);
                    }
                }
            }, stoppingToken));
        }
        await Task.WhenAll(commonAssetsTasks);

        _logger.LogDebug("Fetching runner token entities from service...");
        var runnerTokenEntityMap = (await runnerTokenService.GetAll(stoppingToken)).GetValueOrThrow();

        _logger.LogDebug("Fetching runner entities from service...");
        var runnerEntityMap = (await runnerService.GetAll(stoppingToken)).GetValueOrThrow();

        _logger.LogDebug("Fetching docker containers from service...");
        var dockerContainers = await dockerService.GetContainers();

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
                _logger.LogError("Error fetching runners for runner token {}: {}", name, runnerListResult.Error!.Message);
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
                        bool busy = runnerJson.GetProperty("busy").GetBoolean();
                        var runnerId = nameSplit[2];
                        var runnerAction = new RunnerAction()
                        {
                            Id = id,
                            RunnerId = runnerId,
                            Name = name,
                            Busy = busy
                        };
                        runnerActions.Add(runnerAction);
                        runnerActionMap[name] = (runnerAction, runnerToken);
                    }
                }
            }
            runnerTokenMap[runnerToken.Id] = (runnerToken, runnerActions);
        }

        _logger.LogDebug("Checking cache server...");
        if (!dockerContainers.Any(i => i.Labels.TryGetValue("cicd.self_runner_cache_for", out _)))
        {
            try
            {
                _logger.LogInformation("Cache server not found. Adding cache server...");
                RunnerOSType runnerOS = RunnerOSType.Linux;
                string name = $"managed_runner-{runnerControllerId}-cache_container-{StringHelpers.Random(6, false).ToLowerInvariant()}";
                string image = "ghcr.io/falcondev-oss/github-actions-cache-server:latest";
                string runnerId = $"managed_runner-{runnerControllerId}";
                string dockerArgs = "";
                dockerArgs += $" -l \"cicd.self_runner_cache_for={runnerControllerId}\"";
                dockerArgs += $" -e \"URL_ACCESS_TOKEN=awdawd\"";
                dockerArgs += $" -e \"API_BASE_URL=http://localhost:3000\"";
                dockerArgs += $" -v \"cache-data-{runnerControllerId}:/app/.data\"";
                dockerArgs += $" -p \"3000:3000\"";
                dockerArgs += " --add-host host.docker.internal=host-gateway";
                await dockerService.Build(runnerOS, image, runnerId);
                await dockerService.Run(runnerOS, name, image, runnerId, 2, 4, null, dockerArgs);
                _logger.LogInformation("Cache server created: {id}", name);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Runner cache container init error: {ex}", ex.Message);
            }
        }

        _logger.LogDebug("Updating runner entities to runtime runners...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values.ToArray())
        {
            if (runnerEntityMap.TryGetValue(runnerRuntime.RunnerId, out var runnerEntity))
            {
                runnerRuntimeMap[runnerRuntime.RunnerId] = new()
                {
                    TokenId = runnerRuntime.TokenId,
                    TokenRev = runnerRuntime.TokenRev,
                    RunnerId = runnerEntity.Id,
                    RunnerRev = runnerRuntime.RunnerRev,
                    RunnerTokenEntity = runnerRuntime.RunnerTokenEntity,
                    RunnerEntity = runnerEntity,
                    Runners = runnerRuntime.Runners,
                };
            }
        }

        _logger.LogDebug("Updating runner token entities to runtime runners...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values.ToArray())
        {
            if (runnerTokenEntityMap.TryGetValue(runnerRuntime.TokenId, out var runnerTokenEntity))
            {
                runnerRuntimeMap[runnerRuntime.RunnerId] = new()
                {
                    TokenId = runnerTokenEntity.Id,
                    TokenRev = runnerRuntime.TokenRev,
                    RunnerId = runnerRuntime.RunnerId,
                    RunnerRev = runnerRuntime.RunnerRev,
                    RunnerTokenEntity = runnerTokenEntity,
                    RunnerEntity = runnerRuntime.RunnerEntity,
                    Runners = runnerRuntime.Runners,
                };
            }
        }

        _logger.LogDebug("Updating docker containers to runtime runners...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            foreach (var runner in runnerRuntime.Runners.Values)
            {
                var dockerContainer = dockerContainers.FirstOrDefault(i =>
                    i.Labels.TryGetValue("cicd.self_runner_id", out var containerRunnerId) &&
                    i.Labels.TryGetValue("cicd.self_runner_name", out var containerRunnerName) &&
                    containerRunnerId == runnerRuntime.RunnerId &&
                    containerRunnerName == runner.Name);
                runnerRuntime.Runners[runner.Name] = new()
                {
                    Name = runner.Name,
                    DockerContainer = dockerContainer,
                    RunnerAction = runner.RunnerAction,
                    Status = runner.Status,
                };
            }
        }

        _logger.LogDebug("Updating action runners to runtime runners...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            foreach (var runner in runnerRuntime.Runners.Values)
            {
                var (RunnerAction, _) = runnerActionMap.GetValueOrDefault(runner.Name);
                runnerRuntime.Runners[runner.Name] = new()
                {
                    Name = runner.Name,
                    DockerContainer = runner.DockerContainer,
                    RunnerAction = RunnerAction,
                    Status = runner.Status,
                };
            }
        }

        _logger.LogDebug("Adding runner entities and runner token entities to runtime runners...");
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

        _logger.LogDebug("Adding docker containers to runtime runners...");
        foreach (var dockerContainer in dockerContainers)
        {
            if (dockerContainer.Labels.TryGetValue("cicd.self_runner_id", out var containerRunnerId) &&
                dockerContainer.Labels.TryGetValue("cicd.self_runner_name", out var containerRunnerName))
            {
                if (!runnerRuntimeMap.TryGetValue(containerRunnerId, out var runnerRuntime))
                {
                    continue;
                }
                if (runnerRuntime.Runners.ContainsKey(containerRunnerName))
                {
                    continue;
                }
                var (RunnerAction, _) = runnerActionMap.GetValueOrDefault(containerRunnerName);
                runnerRuntime.Runners[containerRunnerName] = new()
                {
                    Name = containerRunnerName,
                    DockerContainer = dockerContainer,
                    RunnerAction = RunnerAction,
                    Status = RunnerStatus.Building,
                };
            }
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
            var dockerContainer = dockerContainers.FirstOrDefault(i =>
                i.Labels.TryGetValue("cicd.self_runner_id", out var containerRunnerId) &&
                i.Labels.TryGetValue("cicd.self_runner_name", out var containerRunnerName) &&
                containerRunnerId == runnerRuntime.RunnerId &&
                containerRunnerName == RunnerAction.Name);
            runnerRuntime.Runners[RunnerAction.Name] = new()
            {
                Name = RunnerAction.Name,
                DockerContainer = dockerContainer,
                RunnerAction = RunnerAction,
                Status = RunnerStatus.Building,
            };
        }

        _logger.LogDebug("Updating runtime runner instance status...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            foreach (var runner in runnerRuntime.Runners.Values.ToArray())
            {
                RunnerStatus runnerStatus = RunnerStatus.Building;
                if (runner.DockerContainer != null && runner.RunnerAction != null)
                {
                    runnerStatus = runner.RunnerAction.Busy ? RunnerStatus.Busy : RunnerStatus.Ready;
                }
                else if (runner.DockerContainer != null && runner.RunnerAction == null)
                {
                    runnerStatus = RunnerStatus.Starting;
                }
                runnerRuntime.Runners[runner.Name] = new()
                {
                    Name = runner.Name,
                    DockerContainer = runner.DockerContainer,
                    RunnerAction = runner.RunnerAction,
                    Status = runnerStatus
                };
            }
        }

        _logger.LogDebug("Removing deleted runner tokens...");
        foreach (var runnerTokenEntity in runnerTokenEntityMap.Values)
        {
            if (!runnerTokenEntity.Deleted)
            {
                continue;
            }
            foreach (var runnerRuntime in runnerRuntimeMap.Values.Where(i => i.TokenId == runnerTokenEntity.Id))
            {
                (await runnerService.Delete(runnerRuntime.RunnerEntity.Id, false, stoppingToken)).ThrowIfError();
                foreach (var runner in runnerRuntime.Runners.Values.ToArray())
                {
                    if (runner.RunnerAction != null)
                    {
                        var deleteResult = await Execute(httpClient, HttpMethod.Delete, runnerTokenEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                        deleteResult.ThrowIfError();
                    }
                    if (runner.DockerContainer != null)
                    {
                        await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                    }
                    runnerRuntime.Runners.Remove(runner.Name);
                    _logger.LogInformation("Runner purged (token deleted): {name}", runner.Name);
                }
                (await runnerService.Delete(runnerRuntime.RunnerId, true, stoppingToken)).ThrowIfError();
                runnerRuntimeMap.Remove(runnerRuntime.RunnerId);
                _logger.LogInformation("Runner instance purged (token deleted): {name}", runnerRuntime.RunnerId);
            }
            (await runnerTokenService.Delete(runnerTokenEntity.Id, true, stoppingToken)).ThrowIfError();
            _logger.LogInformation("Runner token purged (token deleted): {name}", runnerTokenEntity.Id);
        }

        _logger.LogDebug("Removing deleted runners...");
        foreach (var runnerEntity in runnerEntityMap.Values)
        {
            if (!runnerEntity.Deleted)
            {
                continue;
            }
            if (!runnerTokenEntityMap.TryGetValue(runnerEntity.TokenId, out var runnerTokenEntity))
            {
                continue;
            }
            if (!runnerRuntimeMap.TryGetValue(runnerEntity.Id, out var runnerRuntime))
            {
                continue;
            }
            foreach (var runner in runnerRuntime.Runners.Values.ToArray())
            {
                if (runner.DockerContainer != null)
                {
                    await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                }
                if (runner.RunnerAction != null)
                {
                    var deleteResult = await Execute(httpClient, HttpMethod.Delete, runnerTokenEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                    deleteResult.ThrowIfError();
                }
                runnerRuntime.Runners.Remove(runner.Name);
                _logger.LogInformation("Runner purged (runner deleted): {name}", runner.Name);
            }
            (await runnerService.Delete(runnerEntity.Id, true, stoppingToken)).ThrowIfError();
            runnerRuntimeMap.Remove(runnerEntity.Id);
            _logger.LogInformation("Runner instance purged (runner deleted): {name}", runnerEntity.Id);
        }

        _logger.LogDebug("Removing dangling runner actions...");
        foreach (var (RunnerAction, RunnerTokenEntity) in runnerActionMap.Values)
        {
            bool delete = false;
            foreach (var runnerRuntime in runnerRuntimeMap.Values)
            {
                foreach (var runner in runnerRuntime.Runners.Values.ToArray())
                {
                    if (runner.Name == RunnerAction.Name && runner.DockerContainer == null)
                    {
                        runnerRuntime.Runners.Remove(runner.Name);
                        delete = true;
                        break;
                    }
                }
                if (delete)
                {
                    break;
                }
            }
            if (delete)
            {
                (await Execute(httpClient, HttpMethod.Delete, RunnerTokenEntity, $"actions/runners/{RunnerAction.Id}", stoppingToken)).ThrowIfError();
                _logger.LogInformation("Runner action purged (dangling): {name}", RunnerAction.Name);
            }
        }
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            foreach (var runner in runnerRuntime.Runners.Values)
            {
                if (runner.DockerContainer == null && runner.RunnerAction != null)
                {
                    if (!runnerTokenEntityMap.TryGetValue(runnerRuntime.TokenId, out var runnerTokenEntity))
                    {
                        continue;
                    }
                    (await Execute(httpClient, HttpMethod.Delete, runnerTokenEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken)).ThrowIfError();
                    _logger.LogInformation("Runner action purged (dangling): {name}", runner.RunnerAction.Name);
                }
            }
        }

        _logger.LogDebug("Removing dangling images...");
        List<(DockerImage DockerImage, RunnerOSType RunnerOS)> allDockerImages = [];
        allDockerImages.AddRange((await dockerService.GetImages(RunnerOSType.Linux)).Select(i => (i, RunnerOSType.Linux)));
        allDockerImages.AddRange((await dockerService.GetImages(RunnerOSType.Windows)).Select(i => (i, RunnerOSType.Windows)));
        foreach (var (image, runnerOS) in allDockerImages)
        {
            string[] repoSplit = image.Repository.Split('-');
            if (repoSplit.Length >= 3 && repoSplit[0] == "managed_runner" && repoSplit[1] == runnerControllerId)
            {
                string containerRunnerId = repoSplit[2];
                string tag = image.Tag;
                if (!runnerRuntimeMap.TryGetValue(containerRunnerId, out var runnerRuntime))
                {
                    var imageToDelete = $"{image.Repository}:{tag}";
                    await dockerService.DeleteImages(runnerOS, imageToDelete);
                    _logger.LogInformation("Runner image purged (outdated): {name}", imageToDelete);
                }
            }
        }

        _logger.LogDebug("Removing dangling runners...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            foreach (var runner in runnerRuntime.Runners.Values.ToArray())
            {
                if (runner.Status == RunnerStatus.Building && !building.ContainsKey(runner.Name))
                {
                    runnerRuntime.Runners.Remove(runner.Name);
                }
            }
        }

        _logger.LogDebug("Removing outdated runners...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values.ToArray())
        {
            if (runnerRuntime.TokenRev == runnerRuntime.RunnerTokenEntity.Rev &&
                runnerRuntime.RunnerRev == runnerRuntime.RunnerEntity.Rev)
            {
                continue;
            }
            foreach (var runner in runnerRuntime.Runners.Values.ToArray())
            {
                if (runner.DockerContainer != null)
                {
                    await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                }
                if (runner.RunnerAction != null)
                {
                    var deleteResult = await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerTokenEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                    deleteResult.ThrowIfError();
                }
                runnerRuntime.Runners.Remove(runner.Name);
                _logger.LogInformation("Runner purged (outdated): {name}", runner.Name);
            }
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
        }

        _logger.LogDebug("Removing excess runners...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values.ToArray())
        {
            while (runnerRuntime.RunnerEntity.Count < runnerRuntime.Runners.Count)
            {
                var runner = runnerRuntime.Runners.Values.FirstOrDefault();
                if (runner == null)
                {
                    break;
                }
                if (runner.DockerContainer != null)
                {
                    await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                }
                if (runner.RunnerAction != null)
                {
                    var deleteResult = await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerTokenEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                    deleteResult.ThrowIfError();
                }
                runnerRuntime.Runners.Remove(runner.Name);
                _logger.LogInformation("Runner purged (excess): {name}", runner.Name);
            }
        }

        _logger.LogDebug("Checking for runners to upscale...");
        foreach (var runnerRuntime in runnerRuntimeMap.Values)
        {
            if (!runnerTokenEntityMap.TryGetValue(runnerRuntime.TokenId, out var runnerToken))
            {
                continue;
            }
            if (runnerRuntime.RunnerEntity.Count > runnerRuntime.Runners.Count)
            {
                string? regToken = null;
                try
                {
                    var tokenResponse = await Execute<Dictionary<string, string>>(httpClient, HttpMethod.Post, runnerToken, "actions/runners/registration-token", stoppingToken);
                    tokenResponse.ThrowIfErrorOrHasNoValue();
                    regToken = tokenResponse.Value["token"];
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("Runner register token error: {ex}", ex.Message);
                    continue;
                }

                var runners = runnerRuntime.Runners.ToArray();
                var runnerRev = runnerRuntime.RunnerRev;
                var runnerTokenRev = runnerRuntime.TokenRev;

                for (int i = 0; i < (runnerRuntime.RunnerEntity.Count - runners.Length); i++)
                {
                    string name = $"managed_runner-{runnerControllerId}-{runnerRuntime.RunnerEntity.Id.ToLowerInvariant()}-{runnerRev.ToLowerInvariant()}-{StringHelpers.Random(6, false).ToLowerInvariant()}";
                    try
                    {
                        runnerRuntime.Runners[name] = new RunnerInstance()
                        {
                            Name = name,
                            DockerContainer = null,
                            RunnerAction = null,
                            Status = RunnerStatus.Building
                        };
                        string inputArgs = $"--name {name} --url {GetConfigUrl(runnerToken)} --ephemeral --unattended";
                        inputArgs += $" --token {regToken}";
                        if (runnerRuntime.RunnerEntity.Labels.Length != 0)
                        {
                            inputArgs += $" --no-default-labels --labels {string.Join(',', runnerRuntime.RunnerEntity.Labels)}";
                        }
                        if (!string.IsNullOrEmpty(runnerRuntime.RunnerEntity.Group))
                        {
                            inputArgs += $" --runnergroup {runnerRuntime.RunnerEntity.Group}";
                        }
                        _logger.LogInformation("Runner preparing: {id}", name);
                        RunnerOSType runnerOs = runnerRuntime.RunnerEntity.RunnerOS;
                        string runnerOsStr = runnerOs switch
                        {
                            RunnerOSType.Linux => "linux",
                            RunnerOSType.Windows => "windows",
                            _ => throw new NotSupportedException()
                        };
                        string image = runnerRuntime.RunnerEntity.Image;
                        string runnerId = runnerRuntime.RunnerEntity.Id;
                        var baseImage = $"managed_runner-{runnerControllerId}-{runnerId}-base:latest";
                        var actualImage = $"managed_runner-{runnerControllerId}-{runnerId}:latest";
                        int cpus = runnerRuntime.RunnerEntity.Cpus;
                        int memoryGB = runnerRuntime.RunnerEntity.MemoryGB;
                        string input;
                        string dockerArgs = "";
                        string dockerfile = $"""
                                FROM {baseImage}
                                COPY ./HostAssets/{runnerOsStr} /runner

                                """;
                        if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Linux)
                        {
                            //dockerfile += """
                            //    WORKDIR "/runner"
                            //    SHELL ["/bin/bash", "-c"]
                            //    RUN tar xzf ./actions-runner-linux-x64.tar.gz
                            //    RUN sed -i 's/\x41\x00\x43\x00\x54\x00\x49\x00\x4F\x00\x4E\x00\x53\x00\x5F\x00\x43\x00\x41\x00\x43\x00\x48\x00\x45\x00\x5F\x00\x55\x00\x52\x00\x4C\x00/\x41\x00\x43\x00\x54\x00\x49\x00\x4F\x00\x4E\x00\x53\x00\x5F\x00\x43\x00\x41\x00\x43\x00\x48\x00\x45\x00\x5F\x00\x4F\x00\x52\x00\x4C\x00/g' ./bin/Runner.Worker.dll
                            //    RUN ./bin/installdependencies.sh
                            //    """;
                            dockerfile += """
                                WORKDIR "/runner"
                                SHELL ["/bin/bash", "-c"]
                                RUN tar xzf ./actions-runner-linux-x64.tar.gz
                                RUN ./bin/installdependencies.sh
                                """;
                            input = $"""
                                cd /runner
                                ./config.sh {inputArgs}
                                ACTIONS_CACHE_URL="http://host.docker.internal:3000/awdawd/" ./run.sh
                                """
                                .Replace("\n\r", " && ")
                                .Replace("\r\n", " && ")
                                .Replace("\n", " && ")
                                .Replace("\"", "\\\"");
                        }
                        else if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Windows)
                        {
                            //dockerfile += """
                            //    WORKDIR "C:\runner"
                            //    SHELL ["powershell"]
                            //    RUN Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -Path "$PWD/actions-runner-win-x64.zip").Path, (Resolve-Path -Path "$PWD").Path)
                            //    RUN (gc ./bin/Runner.Worker.dll) -replace ([Text.Encoding]::ASCII.GetString([byte[]] (0x41,0x00,0x43,0x00,0x54,0x00,0x49,0x00,0x4F,0x00,0x4E,0x00,0x53,0x00,0x5F,0x00,0x43,0x00,0x41,0x00,0x43,0x00,0x48,0x00,0x45,0x00,0x5F,0x00,0x55,0x00,0x52,0x00,0x4C,0x00))), ([Text.Encoding]::ASCII.GetString([byte[]] (0x41,0x00,0x43,0x00,0x54,0x00,0x49,0x00,0x4F,0x00,0x4E,0x00,0x53,0x00,0x5F,0x00,0x43,0x00,0x41,0x00,0x43,0x00,0x48,0x00,0x45,0x00,0x5F,0x00,0x4F,0x00,0x52,0x00,0x4C,0x00))) | Set-Content ./bin/Runner.Worker.dll
                            //    """;
                            dockerfile += """
                                WORKDIR "C:\runner"
                                SHELL ["powershell"]
                                RUN Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -Path "$PWD/actions-runner-win-x64.zip").Path, (Resolve-Path -Path "$PWD").Path)
                                """;
                            input = $"""
                                $ErrorActionPreference='Stop' ; $verbosePreference='Continue'
                                cd C:\runner
                                ./config.cmd {inputArgs}
                                $env:ACTIONS_CACHE_URL="http://host.docker.internal:3000/awdawd/"; ./run.cmd
                                """
                                .Replace("\n\r", " ; ")
                                .Replace("\r\n", " ; ")
                                .Replace("\n", " ; ")
                                .Replace("\"", "\\\"");
                        }
                        else
                        {
                            throw new NotSupportedException();
                        }
                        dockerArgs += $" -l \"cicd.self_runner_id={runnerRuntime.RunnerEntity.Id}\"";
                        dockerArgs += $" -l \"cicd.self_runner_rev={runnerRev}\"";
                        dockerArgs += $" -l \"cicd.self_runner_token_rev={runnerTokenRev}\"";
                        dockerArgs += $" -l \"cicd.self_runner_name={name}\"";
                        dockerArgs += $" -e \"RUNNER_ALLOW_RUNASROOT=1\"";
                        dockerArgs += " --add-host host.docker.internal=host-gateway";
                        var actionsRunnerDockerfile = (ActionsRunnerDockerfilesDir / $"managed_runner-{runnerControllerId}-{runnerRuntime.RunnerEntity.Id.ToLowerInvariant()}");
                        await actionsRunnerDockerfile.WriteAllTextAsync(dockerfile, stoppingToken);

                        async void run()
                        {
                            try
                            {
                                building[name] = runnerRuntime.Runners[name];
                                await executorLocker.Execute(actualImage, async () =>
                                {
                                    await dockerService.Build(runnerOs, image, baseImage);
                                    await dockerService.Build(runnerOs, actionsRunnerDockerfile, actualImage);
                                    await dockerService.Run(runnerOs, name, actualImage, runnerId, cpus, memoryGB, input, dockerArgs);

                                    _logger.LogInformation("Runner created (up): {id}", name);
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogInformation("Runner rev run error: {ex}", ex.Message);
                                runnerRuntime.Runners.Remove(name);
                            }
                            finally
                            {
                                building.Remove(name, out _);
                            }
                        }
                        run();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation("Runner rev init error: {ex}", ex.Message);
                        runnerRuntime.Runners.Remove(name);
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
}
