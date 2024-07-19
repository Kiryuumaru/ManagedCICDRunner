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
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private readonly List<string> busyRevs = [];
    private readonly SemaphoreSlim busyRevsLocker = new(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RoutineExecutor.Execute(TimeSpan.FromSeconds(2), stoppingToken, Routine, ex => _logger.LogError("Container runner error: {msg}", ex.Message));
        return Task.CompletedTask;
    }

    private async Task Routine(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var runnerService = scope.ServiceProvider.GetRequiredService<RunnerService>();
        var dockerService = scope.ServiceProvider.GetRequiredService<DockerService>();
        var localStore = scope.ServiceProvider.GetRequiredService<LocalStoreService>();
        var runnerRuntimeHolder = scope.ServiceProvider.GetSingletonObjectHolder<RunnerRuntime[]>();

        string? runnerControllerId = (await localStore.Get<string>("runner_controller_id", cancellationToken: stoppingToken)).Value;
        if (string.IsNullOrEmpty(runnerControllerId))
        {
            runnerControllerId = StringHelpers.Random(6, false).ToLowerInvariant();
            (await localStore.Set("runner_controller_id", runnerControllerId, cancellationToken: stoppingToken)).ThrowIfError();
        }

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "ManagedCICDRunner");

        List<RunnerRuntime> runnerRuntimes = (await runnerService.GetAll(stoppingToken))
            .GetValueOrThrow()
            .Select(i => new RunnerRuntime()
            {
                Id = i.Id,
                Rev = i.Rev,
                RunnerEntity = i
            })
            .ToList();

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

        List<DockerContainer> allDockerContainers = [];
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
                        RunnerStatus status = RunnerStatus.Building;
                        if (runner.RunnerAction != null)
                        {
                            status = runner.RunnerAction.Busy ? RunnerStatus.Busy : RunnerStatus.Ready;
                        }
                        runner = new RunnerInstance()
                        {
                            Name = containerRunnerName,
                            RunnerAction = runner.RunnerAction,
                            DockerContainer = container,
                            Status = status
                        };
                    }
                    else
                    {
                        runner = new RunnerInstance()
                        {
                            Name = containerRunnerName,
                            RunnerAction = null,
                            DockerContainer = container,
                            Status = RunnerStatus.Building
                        };
                    }
                    runnerRuntime.Runners[containerRunnerName] = runner;
                }
            }
        }

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
                        if (runner.DockerContainer.Labels.TryGetValue("cicd.self_runner_rev", out var containerRunnerRev) ||
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
                            string args = $"--name {name} --url {GetConfigUrl(runnerRuntime.RunnerEntity)} --ephemeral --unattended";
                            var tokenResponse = await Execute<Dictionary<string, string>>(httpClient, HttpMethod.Post, runnerRuntime.RunnerEntity, "actions/runners/registration-token", stoppingToken);
                            args += $" --token {tokenResponse["token"]}";
                            Dictionary<string, string> labels = [];
                            labels["cicd.self_runner_id"] = runnerRuntime.RunnerEntity.Id;
                            labels["cicd.self_runner_rev"] = runnerRuntime.RunnerEntity.Rev;
                            labels["cicd.self_runner_name"] = name;
                            Dictionary<string, string> envVars = [];
                            envVars["RUNNER_ALLOW_RUNASROOT"] = "1";
                            if (runnerRuntime.RunnerEntity.Labels.Length != 0)
                            {
                                args += $" --no-default-labels --labels {string.Join(',', runnerRuntime.RunnerEntity.Labels)}";
                            }
                            if (!string.IsNullOrEmpty(runnerRuntime.RunnerEntity.Group))
                            {
                                args += $" --runnergroup {runnerRuntime.RunnerEntity.Group}";
                            }
                            string input = GetInputCommand(runnerRuntime.RunnerEntity.RunnerOS, args);
                            _logger.LogInformation("Runner preparing: {id}", name);
                            await dockerService.Build(
                                runnerRuntime.RunnerEntity.RunnerOS,
                                runnerRuntime.RunnerEntity.Image,
                                runnerRuntime.RunnerEntity.Id);
                            await dockerService.DeleteContainer(runnerRuntime.RunnerEntity.RunnerOS, name);
                            await dockerService.Run(
                                runnerRuntime.RunnerEntity.RunnerOS,
                                name,
                                runnerRuntime.RunnerEntity.Image,
                                runnerRuntime.RunnerEntity.Id,
                                labels,
                                runnerRuntime.RunnerEntity.Cpus,
                                runnerRuntime.RunnerEntity.MemoryGB,
                                input,
                                envVars);
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

    private static string GetInputCommand(RunnerOSType runnerOS, string args)
    {
        string input;
        if (runnerOS == RunnerOSType.Linux)
        {
            input = $"""
                mkdir actions-runner && cd actions-runner
                curl -o actions-runner-linux-x64-2.317.0.tar.gz -L https://github.com/actions/runner/releases/download/v2.317.0/actions-runner-linux-x64-2.317.0.tar.gz
                tar xzf ./actions-runner-linux-x64-2.317.0.tar.gz
                ./bin/installdependencies.sh
                ./config.sh {args}
                ./run.sh
                """
                .Replace("\n\r", " && ")
                .Replace("\r\n", " && ")
                .Replace("\n", " && ")
                .Replace("\"", "\\\"");
        }
        else if (runnerOS == RunnerOSType.Windows)
        {
            input = $"""
                $ErrorActionPreference='Stop' ; $ProgressPreference='Continue' ; $verbosePreference='Continue' ; mkdir actions-runner ; cd actions-runner
                Invoke-WebRequest -Uri https://github.com/actions/runner/releases/download/v2.317.0/actions-runner-win-x64-2.317.0.zip -OutFile actions-runner-win-x64-2.317.0.zip
                Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory("$PWD/actions-runner-win-x64-2.317.0.zip", "$PWD")
                ./config.cmd {args}
                ./run.cmd
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
        return input;
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
