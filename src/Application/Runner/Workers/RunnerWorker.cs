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
using static System.Net.Mime.MediaTypeNames;

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

    private readonly Dictionary<string, RunnerRuntime> runnerRuntimes = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromSeconds(5), stoppingToken, Routine, ex => _logger.LogError("Runner error: {msg}", ex.Message));
        return Task.CompletedTask;
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var runnerService = scope.ServiceProvider.GetRequiredService<RunnerService>();
        var dockerService = scope.ServiceProvider.GetRequiredService<DockerService>();
        var localStore = scope.ServiceProvider.GetRequiredService<LocalStoreService>();
        var runnerRuntimeHolder = scope.ServiceProvider.GetSingletonObjectHolder<RunnerRuntime[]>();

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

        _logger.LogDebug("Checking common assets...");
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

        _logger.LogDebug("Fetching runner entities...");

        foreach (var runnerEntity in (await runnerService.GetAll(stoppingToken)).GetValueOrThrow())
        {
            RunnerRuntime runnerRuntime;
            if (runnerRuntimes.TryGetValue(runnerEntity.Id, out var existingRunnerRuntime))
            {
                runnerRuntime = new RunnerRuntime()
                {
                    Id = runnerEntity.Id,
                    Rev = runnerEntity.Rev,
                    RunnerEntity = runnerEntity,
                    Runners = existingRunnerRuntime.Runners
                };
            }
            else
            {
                runnerRuntime = new RunnerRuntime()
                {
                    Id = runnerEntity.Id,
                    Rev = runnerEntity.Rev,
                    RunnerEntity = runnerEntity
                };
            }
            runnerRuntimes[runnerRuntime.Id] = runnerRuntime;
        }

        _logger.LogDebug("Runner entities: {x}", string.Join(", ", runnerRuntimes.Select(i => i.Value.Id)));

        _logger.LogDebug("Fetching runner actions...");

        List<(string RunnerId, RunnerEntity RunnerEntity, RunnerAction RunnerAction)> allRunnerActions = [];
        Dictionary<string, JsonDocument> cachedGetRunnersAction = [];
        foreach (var runnerRuntime in runnerRuntimes.Values)
        {
            var runnerListResult = await Execute<JsonDocument>(httpClient, HttpMethod.Get, runnerRuntime.RunnerEntity, "actions/runners", stoppingToken);
            if (runnerListResult.IsError && runnerListResult.StatusCode == HttpStatusCode.Unauthorized)
            {
                continue;
            }
            runnerListResult.ThrowIfErrorOrHasNoValue();
            foreach (var runnerJson in runnerListResult.Value.RootElement.GetProperty("runners").EnumerateArray())
            {
                string name = runnerJson.GetProperty("name").GetString()!;
                var nameSplit = name.Split('-');
                if (nameSplit.Length == 5 && nameSplit[0] == "managed_runner" && nameSplit[1] == runnerControllerId)
                {
                    string id = runnerJson.GetProperty("id").GetInt32().ToString();
                    bool busy = runnerJson.GetProperty("busy").GetBoolean();
                    var runnerId = nameSplit[2];
                    if (!allRunnerActions.Any(i => i.RunnerAction.Name == name))
                    {
                        allRunnerActions.Add((runnerId, runnerRuntime.RunnerEntity, new RunnerAction()
                        {
                            Id = id,
                            Name = name,
                            Busy = busy
                        }));
                    }
                }
            }
        }

        _logger.LogDebug("Runner actions: {x}", string.Join(", ", allRunnerActions.Select(i => i.RunnerAction.Name)));

        _logger.LogDebug("Checking for dangling actions...");

        foreach (var (RunnerId, RunnerEntity, RunnerAction) in allRunnerActions.ToArray())
        {
            var nameSplit = RunnerAction.Name.Split('-');
            if (!runnerRuntimes.TryGetValue(RunnerId, out var runnerRuntime))
            {
                var deleteResult = await Execute(httpClient, HttpMethod.Delete, RunnerEntity, $"actions/runners/{RunnerAction.Id}", stoppingToken);
                deleteResult.ThrowIfError();
                _logger.LogInformation("Runner purged (dangling): {name}", RunnerAction.Name);
            }
            else
            {
                runnerRuntime.Runners[RunnerAction.Name] = new RunnerInstance()
                {
                    Name = RunnerAction.Name,
                    RunnerAction = RunnerAction,
                    DockerContainer = null,
                    Status = RunnerStatus.Building
                };
            }
        }

        _logger.LogDebug("Fetching runner containers...");

        List<DockerContainer> allDockerContainers = [];
        allDockerContainers.AddRange(await dockerService.GetContainers(RunnerOSType.Linux));
        allDockerContainers.AddRange(await dockerService.GetContainers(RunnerOSType.Windows));
        DockerContainer? cacheContainer = null;
        foreach (var container in allDockerContainers)
        {
            if (container.Labels.TryGetValue("cicd.self_runner_id", out var containerRunnerId) &&
                container.Labels.TryGetValue("cicd.self_runner_name", out var containerRunnerName))
            {
                if (runnerRuntimes.TryGetValue(containerRunnerId, out var runnerRuntime))
                {
                    if (runnerRuntime.Runners.TryGetValue(container.Name, out var runner))
                    {
                        runner = new RunnerInstance()
                        {
                            Name = containerRunnerName,
                            RunnerAction = runner.RunnerAction,
                            DockerContainer = container,
                            Status = RunnerStatus.Building
                        };
                    }
                    else
                    {
                        runner = new RunnerInstance()
                        {
                            Name = containerRunnerName,
                            RunnerAction = null,
                            DockerContainer = container,
                            Status = RunnerStatus.Starting
                        };
                    }
                    runnerRuntime.Runners[containerRunnerName] = runner;
                }
            }
            else if (container.Labels.TryGetValue("cicd.self_runner_cache_for", out _))
            {
                cacheContainer = container;
            }
        }

        if (cacheContainer == null)
        {
            try
            {
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
                _logger.LogInformation("Runner cache container created (up): {id}", name);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Runner cache container init error: {ex}", ex.Message);
            }
        }

        _logger.LogDebug("Runner containers: {x}", string.Join(", ", allDockerContainers.Select(i => i.Name)));

        _logger.LogDebug("Checking for deleted runners...");

        foreach (var runnerRuntime in runnerRuntimes.Values.ToArray())
        {
            if (runnerRuntime.RunnerEntity.Deleted)
            {
                foreach (var runner in runnerRuntime.Runners.Values)
                {
                    if (runner.RunnerAction != null)
                    {
                        var deleteResult = await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                        deleteResult.ThrowIfError();
                        _logger.LogInformation("Runner purged (deleted): {name}", runner.Name);
                    }
                    if (runner.DockerContainer != null)
                    {
                        await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                        _logger.LogInformation("Runner purged (deleted): {name}", runner.Name);
                    }
                }
                await runnerService.Delete(runnerRuntime.RunnerEntity.Id, true, stoppingToken);
                runnerRuntimes.Remove(runnerRuntime.Id);
            }
            else
            {
                List<(string Name, string Id, bool Busy)> runners = [];
                foreach (var runner in runnerRuntime.Runners.Values.ToArray())
                {
                    if (runner.DockerContainer == null && runner.RunnerAction != null)
                    {
                        var deleteResult = await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                        deleteResult.ThrowIfError();
                        runnerRuntime.Runners.Remove(runner.Name);
                        _logger.LogInformation("Runner purged (deleted): {name}", runner.Name);
                    }
                    else if (runner.DockerContainer != null && runner.RunnerAction != null)
                    {
                        if (!runner.DockerContainer.Labels.TryGetValue("cicd.self_runner_rev", out var containerRunnerRev) ||
                            containerRunnerRev != runnerRuntime.Rev)
                        {
                            var deleteResult = await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                            deleteResult.ThrowIfError();
                            await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                            runnerRuntime.Runners.Remove(runner.Name);
                            _logger.LogInformation("Runner purged (outdated): {name}", runner.Name);
                        }
                    }
                }
            }
        }

        _logger.LogDebug("Checking for runner status");

        foreach (var runnerRuntime in runnerRuntimes.Values.ToArray())
        {
            foreach (var runner in runnerRuntime.Runners.Values.ToArray())
            {
                RunnerStatus status = RunnerStatus.Building;
                if (runner.DockerContainer != null && runner.RunnerAction != null)
                {
                    status = runner.RunnerAction.Busy ? RunnerStatus.Busy : RunnerStatus.Ready;
                }
                else if (runner.DockerContainer != null)
                {
                    status = RunnerStatus.Starting;
                }
                runnerRuntime.Runners[runner.Name] = new RunnerInstance()
                {
                    Name = runner.Name,
                    RunnerAction = runner.RunnerAction,
                    DockerContainer = runner.DockerContainer,
                    Status = status
                };
            }
        }

        _logger.LogDebug("Checking for dangling images");

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
                if (!runnerRuntimes.TryGetValue(containerRunnerId, out var runnerRuntime))
                {
                    var imageToDelete = $"{image.Repository}:{tag}";
                    await dockerService.DeleteImages(runnerOS, imageToDelete);
                    _logger.LogInformation("Runner image purged (outdated): {name}", imageToDelete);
                }
            }
        }

        _logger.LogDebug("Checking for runners to upscale");

        foreach (var runnerRuntime in runnerRuntimes.Values)
        {
            if (runnerRuntime.RunnerEntity.Count > runnerRuntime.Runners.Count)
            {
                string? regToken = null;
                try
                {
                    var tokenResponse = await Execute<Dictionary<string, string>>(httpClient, HttpMethod.Post, runnerRuntime.RunnerEntity, "actions/runners/registration-token", stoppingToken);
                    tokenResponse.ThrowIfErrorOrHasNoValue();
                    regToken = tokenResponse.Value["token"];
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("Runner register token error: {ex}", ex.Message);
                    continue;
                }

                var runners = runnerRuntime.Runners.ToArray();
                var runnerRev = runnerRuntime.Rev;

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
                        string inputArgs = $"--name {name} --url {GetConfigUrl(runnerRuntime.RunnerEntity)} --ephemeral --unattended";
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
                            input = $"""
                                cd /runner
                                ./config.sh {inputArgs}
                                ACTIONS_CACHE_URL="http://host.docker.internal:3000/awdawd/" ./run.sh
                                """
                                .Replace("\n\r", " && ")
                                .Replace("\r\n", " && ")
                                .Replace("\n", " && ")
                                .Replace("\"", "\\\"");
                            dockerfile += """
                                WORKDIR "/runner"
                                SHELL ["/bin/bash", "-c"]
                                RUN tar xzf ./actions-runner-linux-x64.tar.gz
                                RUN sed -i 's/\x41\x00\x43\x00\x54\x00\x49\x00\x4F\x00\x4E\x00\x53\x00\x5F\x00\x43\x00\x41\x00\x43\x00\x48\x00\x45\x00\x5F\x00\x55\x00\x52\x00\x4C\x00/\x41\x00\x43\x00\x54\x00\x49\x00\x4F\x00\x4E\x00\x53\x00\x5F\x00\x43\x00\x41\x00\x43\x00\x48\x00\x45\x00\x5F\x00\x4F\x00\x52\x00\x4C\x00/g' ./bin/Runner.Worker.dll
                                RUN ./bin/installdependencies.sh
                                """;
                        }
                        else if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Windows)
                        {
                            input = $"""
                                $ErrorActionPreference='Stop' ; $ProgressPreference='Continue' ; $verbosePreference='Continue'
                                cd C:\runner
                                ./config.cmd {inputArgs}
                                $env:ACTIONS_CACHE_URL="http://host.docker.internal:3000/awdawd/"; ./run.cmd
                                """
                                .Replace("\n\r", " ; ")
                                .Replace("\r\n", " ; ")
                                .Replace("\n", " ; ")
                                .Replace("\"", "\\\"");
                            dockerfile += """
                                WORKDIR "C:\runner"
                                SHELL ["powershell"]
                                RUN Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -Path "$PWD/actions-runner-win-x64.zip").Path, (Resolve-Path -Path "$PWD").Path)
                                RUN (gc ./bin/Runner.Worker.dll) -replace ([Text.Encoding]::ASCII.GetString([byte[]] (0x41,0x00,0x43,0x00,0x54,0x00,0x49,0x00,0x4F,0x00,0x4E,0x00,0x53,0x00,0x5F,0x00,0x43,0x00,0x41,0x00,0x43,0x00,0x48,0x00,0x45,0x00,0x5F,0x00,0x55,0x00,0x52,0x00,0x4C,0x00))), ([Text.Encoding]::ASCII.GetString([byte[]] (0x41,0x00,0x43,0x00,0x54,0x00,0x49,0x00,0x4F,0x00,0x4E,0x00,0x53,0x00,0x5F,0x00,0x43,0x00,0x41,0x00,0x43,0x00,0x48,0x00,0x45,0x00,0x5F,0x00,0x4F,0x00,0x52,0x00,0x4C,0x00))) | Set-Content ./bin/Runner.Worker.dll
                                """;
                        }
                        else
                        {
                            throw new NotSupportedException();
                        }
                        dockerArgs += $" -l \"cicd.self_runner_id={runnerRuntime.RunnerEntity.Id}\"";
                        dockerArgs += $" -l \"cicd.self_runner_rev={runnerRev}\"";
                        dockerArgs += $" -l \"cicd.self_runner_name={name}\"";
                        dockerArgs += $" -e \"RUNNER_ALLOW_RUNASROOT=1\"";
                        dockerArgs += " --add-host host.docker.internal=host-gateway";
                        var actionsRunnerDockerfile = (ActionsRunnerDockerfilesDir / $"managed_runner-{runnerControllerId}-{runnerRuntime.RunnerEntity.Id.ToLowerInvariant()}");
                        await actionsRunnerDockerfile.WriteAllTextAsync(dockerfile, stoppingToken);

                        async void run()
                        {
                            try
                            {
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

        await runnerRuntimeHolder.Set(() => [.. runnerRuntimes.Values]);

        _logger.LogDebug("Runner routine end");
    }

    private static async Task<HttpResult> Execute(HttpClient httpClient, HttpMethod httpMethod, RunnerEntity runnerEntity, string segement, CancellationToken cancellationToken)
    {
        HttpRequestMessage requestMessage = new(httpMethod, GetEndpoint(runnerEntity, segement));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerEntity.GithubToken);
        return await httpClient.Execute(requestMessage, cancellationToken: cancellationToken);
    }

    private static async Task<HttpResult<T>> Execute<T>(HttpClient httpClient, HttpMethod httpMethod, RunnerEntity runnerEntity, string segement, CancellationToken cancellationToken)
    {
        HttpRequestMessage requestMessage = new(httpMethod, GetEndpoint(runnerEntity, segement));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerEntity.GithubToken);
        return await httpClient.Execute<T>(requestMessage, cancellationToken: cancellationToken);
    }

    private static string GetEndpoint(RunnerEntity runnerEntity, string segement)
    {
        if (!string.IsNullOrEmpty(runnerEntity.GithubOrg) && !string.IsNullOrEmpty(runnerEntity.GithubRepo))
        {
            return $"https://api.github.com/repos/{runnerEntity.GithubOrg}/{runnerEntity.GithubRepo}/{segement}";
        }
        else if (!string.IsNullOrEmpty(runnerEntity.GithubOrg))
        {
            return $"https://api.github.com/orgs/{runnerEntity.GithubOrg}/{segement}";
        }
        else
        {
            throw new Exception("GithubOrg and GithubRepo is empty");
        }
    }

    private static string GetConfigUrl(RunnerEntity runnerEntity)
    {
        if (!string.IsNullOrEmpty(runnerEntity.GithubOrg) && !string.IsNullOrEmpty(runnerEntity.GithubRepo))
        {
            return $"https://github.com/{runnerEntity.GithubOrg}/{runnerEntity.GithubRepo}";
        }
        else if (!string.IsNullOrEmpty(runnerEntity.GithubOrg))
        {
            return $"https://github.com/{runnerEntity.GithubOrg}";
        }
        else
        {
            throw new Exception("GithubOrg and GithubRepo is empty");
        }
    }
}
