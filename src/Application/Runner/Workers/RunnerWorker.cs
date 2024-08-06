using Application.Common;
using Application.LocalStore.Services;
using Application.Runner.Services;
using Application.Vagrant.Services;
using CliWrap.EventStream;
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
    private readonly static AbsolutePath ActionsRunnerVagrantfilesDir = AbsolutePath.Parse(Environment.CurrentDirectory) / "Vagrantfiles" / ".ActionsRunner";

    private readonly static (RunnerOSType OS, string Url, string Hash, AbsolutePath Path)[] HostAssetMatrix =
    [
        (
            RunnerOSType.Linux,
            "https://github.com/actions/runner/releases/download/v2.317.0/actions-runner-linux-x64-2.317.0.tar.gz",
            "9e883d210df8c6028aff475475a457d380353f9d01877d51cc01a17b2a91161d",
            LinuxActionsRunner
        ), (
            RunnerOSType.Windows,
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
        RoutineExecutor.Execute(TimeSpan.FromSeconds(5), false, stoppingToken, Routine, ex => _logger.LogError("Runner error: {msg}", ex.Message));
        return Task.CompletedTask;
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

        _logger.LogDebug("Fetching vagrant instances from service...");
        var vagrantInstances = await vagrantService.GetInstances();

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
