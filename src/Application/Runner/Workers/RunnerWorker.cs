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
using RestfulHelpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
    private readonly static AbsolutePath ActionsRunnerDir = AbsolutePath.Parse(Environment.CurrentDirectory) / "Mount";
    private readonly static AbsolutePath LinuxActionsRunnerDir = ActionsRunnerDir / "linux";
    private readonly static AbsolutePath WindowsActionsRunnerDir = ActionsRunnerDir / "windows";
    private readonly static AbsolutePath LinuxActionsRunner = LinuxActionsRunnerDir / "actions-runner-linux-x64.tar.gz";
    private readonly static AbsolutePath WindowsActionsRunner = WindowsActionsRunnerDir / "actions-runner-win-x64.zip";

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

    private readonly ILogger<RunnerWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly List<string> busyRevs = [];
    private readonly SemaphoreSlim busyRevsLocker = new(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromSeconds(2), stoppingToken, Routine, ex => _logger.LogError("Runner error: {msg}", ex.Message));
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

        List<RunnerRuntime> runnerRuntimes = (await runnerService.GetAll(stoppingToken))
            .GetValueOrThrow()
            .Select(i => new RunnerRuntime()
            {
                Id = i.Id,
                Rev = i.Rev,
                RunnerEntity = i
            })
            .ToList();

        _logger.LogDebug("Runner entities: {x}", string.Join(", ", runnerRuntimes.Select(i => i.Id)));

        _logger.LogDebug("Fetching runner actions...");

        List<(RunnerEntity RunnerEntity, RunnerAction RunnerAction)> allRunnerActions = [];
        foreach (var runnerRuntime in runnerRuntimes)
        {
            var runnerListResult = await Execute<JsonDocument>(httpClient, HttpMethod.Get, runnerRuntime.RunnerEntity, "actions/runners", stoppingToken);
            foreach (var runnerJson in runnerListResult.RootElement.GetProperty("runners").EnumerateArray())
            {
                string name = runnerJson.GetProperty("name").GetString()!;
                var nameSplit = name.Split('-');
                if (nameSplit.Length == 5 && nameSplit[0] == "managed_runner" && nameSplit[1] == runnerControllerId)
                {
                    string id = runnerJson.GetProperty("id").GetInt32().ToString();
                    bool busy = runnerJson.GetProperty("busy").GetBoolean();
                    if (!allRunnerActions.Any(i => i.RunnerAction.Name == name))
                    {
                        allRunnerActions.Add((runnerRuntime.RunnerEntity, new RunnerAction()
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

        foreach (var (RunnerEntity, RunnerAction) in allRunnerActions.ToArray())
        {
            var nameSplit = RunnerAction.Name.Split('-');
            var runnerRuntime = runnerRuntimes.FirstOrDefault(i => i.RunnerEntity.Id == nameSplit[2]);
            if (runnerRuntime == null)
            {
                await Execute(httpClient, HttpMethod.Delete, RunnerEntity, $"actions/runners/{RunnerAction.Id}", stoppingToken);
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
        DockerContainer? cacheContainer = null;
        allDockerContainers.AddRange(await dockerService.GetContainers(RunnerOSType.Linux));
        allDockerContainers.AddRange(await dockerService.GetContainers(RunnerOSType.Windows));
        foreach (var container in allDockerContainers)
        {
            if (container.Labels.TryGetValue("cicd.self_runner_id", out var containerRunnerId) &&
                container.Labels.TryGetValue("cicd.self_runner_name", out var containerRunnerName))
            {
                var runnerRuntime = runnerRuntimes.FirstOrDefault(i => i.RunnerEntity.Id == containerRunnerId);
                if (runnerRuntime != null)
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
            string key = $"managed_runner-{runnerControllerId}-cache_container";
            RevExecute(key, async () =>
            {
                try
                {
                    RunnerOSType runnerOS = RunnerOSType.Linux;
                    string name = $"{key}-{StringHelpers.Random(6, false).ToLowerInvariant()}";
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
            }, () =>
            {
                _logger.LogInformation("Runner cache container init (pending): {rev}", key);
            });
        }

        _logger.LogDebug("Runner containers: {x}", string.Join(", ", allDockerContainers.Select(i => i.Name)));

        _logger.LogDebug("Checking for deleted runners...");

        foreach (var runnerRuntime in runnerRuntimes.ToArray())
        {
            if (runnerRuntime.RunnerEntity.Deleted)
            {
                foreach (var runner in runnerRuntime.Runners.Values)
                {
                    if (runner.RunnerAction != null)
                    {
                        await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                        _logger.LogInformation("Runner purged (deleted): {name}", runner.Name);
                    }
                    if (runner.DockerContainer != null)
                    {
                        await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                        _logger.LogInformation("Runner purged (deleted): {name}", runner.Name);
                    }
                }
                runnerRuntimes.Remove(runnerRuntime);
                await runnerService.Delete(runnerRuntime.RunnerEntity.Id, true, stoppingToken);
            }
            else
            {
                List<(string Name, string Id, bool Busy)> runners = [];
                foreach (var runner in runnerRuntime.Runners.Values.ToArray())
                {
                    if (runner.DockerContainer == null && runner.RunnerAction != null)
                    {
                        await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                        runnerRuntime.Runners.Remove(runner.Name);
                        _logger.LogInformation("Runner purged (deleted): {name}", runner.Name);
                    }
                    else if (runner.DockerContainer != null && runner.RunnerAction != null)
                    {
                        if (!runner.DockerContainer.Labels.TryGetValue("cicd.self_runner_rev", out var containerRunnerRev) ||
                            containerRunnerRev != runnerRuntime.Rev)
                        {
                            await Execute(httpClient, HttpMethod.Delete, runnerRuntime.RunnerEntity, $"actions/runners/{runner.RunnerAction.Id}", stoppingToken);
                            await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, runner.DockerContainer.Name);
                            runnerRuntime.Runners.Remove(runner.Name);
                            _logger.LogInformation("Runner purged (outdated): {name}", runner.Name);
                        }
                    }
                }
            }
        }

        _logger.LogDebug("Checking for runner status");

        foreach (var runnerRuntime in runnerRuntimes.ToArray())
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

        _logger.LogDebug("Checking for runners to upscale");

        var oldRunnerRuntimes = await runnerRuntimeHolder.Get() ?? [];

        foreach (var runnerRuntime in runnerRuntimes)
        {
            var oldRunnerRuntime = oldRunnerRuntimes.FirstOrDefault(i => i.RunnerEntity.Id == runnerRuntime.RunnerEntity.Id);
            if (oldRunnerRuntime != null)
            {
                foreach (var oldRunner in oldRunnerRuntime.Runners.Values)
                {
                    if (oldRunner.Status == RunnerStatus.Building &&
                        !runnerRuntime.Runners.ContainsKey(oldRunner.Name))
                    {
                        runnerRuntime.Runners[oldRunner.Name] = oldRunner;
                    }
                }
            }
            if (runnerRuntime.RunnerEntity.Count > runnerRuntime.Runners.Count)
            {
                string key = $"managed_runner-{runnerControllerId}-{runnerRuntime.RunnerEntity.Id.ToLowerInvariant()}-{runnerRuntime.RunnerEntity.Rev.ToLowerInvariant()}";
                RevExecute(key, async () =>
                {
                    var runners = runnerRuntime.Runners.ToArray();
                    for (int i = 0; i < (runnerRuntime.RunnerEntity.Count - runners.Length); i++)
                    {
                        string name = $"managed_runner-{runnerControllerId}-{runnerRuntime.RunnerEntity.Id.ToLowerInvariant()}-{runnerRuntime.RunnerEntity.Rev.ToLowerInvariant()}-{StringHelpers.Random(6, false).ToLowerInvariant()}";
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
                            var tokenResponse = await Execute<Dictionary<string, string>>(httpClient, HttpMethod.Post, runnerRuntime.RunnerEntity, "actions/runners/registration-token", stoppingToken);
                            inputArgs += $" --token {tokenResponse["token"]}";
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
                            string image = runnerRuntime.RunnerEntity.Image;
                            string runnerId = runnerRuntime.RunnerEntity.Id;
                            int cpus = runnerRuntime.RunnerEntity.Cpus;
                            int memoryGB = runnerRuntime.RunnerEntity.MemoryGB;
                            string input;
                            string dockerArgs = "";
                            if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Linux)
                            {
                                input = $"""
                                    mkdir /actions-runner
                                    cd /actions-runner
                                    cp -r /host-assets/* ./
                                    ./bootstrap.sh
                                    ./config.sh {inputArgs}
                                    ACTIONS_CACHE_URL="http://host.docker.internal:3000/awdawd/" ./run.sh
                                    """
                                    .Replace("\n\r", " && ")
                                    .Replace("\r\n", " && ")
                                    .Replace("\n", " && ")
                                    .Replace("\"", "\\\"");
                                var wslPath = LinuxActionsRunnerDir.ToString().Replace("\\", "/").Replace(":", "");
                                wslPath = wslPath[0].ToString().ToLowerInvariant() + wslPath[1..].ToString();
                                wslPath = "/mnt/" + wslPath;
                                dockerArgs += $" -v \"{wslPath}:/host-assets\"";
                            }
                            else if (runnerRuntime.RunnerEntity.RunnerOS == RunnerOSType.Windows)
                            {
                                input = $"""
                                    $ErrorActionPreference='Stop' ; $ProgressPreference='Continue' ; $verbosePreference='Continue'
                                    mkdir C:\actions-runner
                                    cd C:\actions-runner                     
                                    Copy-Item "C:\host-assets\*" -Destination "$PWD" -Recurse -Force
                                    ./bootstrap.ps1
                                    ./config.cmd {inputArgs}
                                    $env:ACTIONS_CACHE_URL="http://host.docker.internal:3000/awdawd/"; ./run.cmd
                                    """
                                    .Replace("\n\r", " ; ")
                                    .Replace("\r\n", " ; ")
                                    .Replace("\n", " ; ")
                                    .Replace("\"", "\\\"");
                                dockerArgs += $" -v \"{WindowsActionsRunnerDir}:C:\\host-assets\"";
                            }
                            else
                            {
                                throw new NotSupportedException();
                            }
                            dockerArgs += $" -l \"cicd.self_runner_id={runnerRuntime.RunnerEntity.Id}\"";
                            dockerArgs += $" -l \"cicd.self_runner_rev={runnerRuntime.RunnerEntity.Rev}\"";
                            dockerArgs += $" -l \"cicd.self_runner_name={name}\"";
                            dockerArgs += $" -e \"RUNNER_ALLOW_RUNASROOT=1\"";
                            dockerArgs += " --add-host host.docker.internal=host-gateway";
                            await dockerService.Build(runnerOs, image, runnerId);
                            try
                            {
                                await dockerService.DeleteContainer(runnerOs, name);
                            }
                            catch { }
                            await dockerService.Run(runnerOs, name, image, runnerId, cpus, memoryGB, input, dockerArgs);
                            _logger.LogInformation("Runner created (up): {id}", name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogInformation("Runner rev init error: {ex}", ex.Message);
                        }
                        finally
                        {
                            runnerRuntime.Runners.Remove(name);
                        }
                    }
                }, () =>
                {
                    _logger.LogInformation("Runner rev init (pending): {rev}", key);
                });
            }
        }

        await runnerRuntimeHolder.Set(() => [.. runnerRuntimes]);

        _logger.LogDebug("Runner routine end");
    }

    private static async Task Execute(HttpClient httpClient, HttpMethod httpMethod, RunnerEntity runnerEntity, string segement, CancellationToken cancellationToken)
    {
        HttpRequestMessage requestMessage = new(httpMethod, GetEndpoint(runnerEntity, segement));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerEntity.GithubToken);
        (await httpClient.Execute(requestMessage, cancellationToken: cancellationToken)).ThrowIfError();
    }

    private static async Task<T> Execute<T>(HttpClient httpClient, HttpMethod httpMethod, RunnerEntity runnerEntity, string segement, CancellationToken cancellationToken)
    {
        HttpRequestMessage requestMessage = new(httpMethod, GetEndpoint(runnerEntity, segement));
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runnerEntity.GithubToken);
        return (await httpClient.Execute<T>(requestMessage, cancellationToken: cancellationToken)).GetValueOrThrow();
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

    private async void RevExecute(string key, Func<Task> exec, Action busy)
    {
        if (await busyRevsLocker.WaitAsync(10))
        {
            bool removeKey = false;
            try
            {
                if (busyRevs.Contains(key))
                {
                    busy();
                }
                else
                {
                    busyRevs.Add(key);
                    removeKey = true;
                    await exec();
                }
            }
            catch
            {
                throw;
            }
            finally
            {
                if (removeKey)
                {
                    busyRevs.Remove(key);
                }
                busyRevsLocker.Release();
            }
        }
        else
        {
            busy();
        }
    }
}
