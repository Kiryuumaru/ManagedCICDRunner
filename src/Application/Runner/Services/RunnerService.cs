using Application.Common;
using Application.LocalStore.Services;
using Application.Runner.Interfaces;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
using Domain.Runner.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Interface;

namespace Application.Runner.Services;

public class RunnerService(ILogger<RunnerService> logger, IServiceProvider serviceProvider, RunnerStoreService runnerStoreService, RunnerTokenStoreService runnerTokenStoreService) : IRunnerService
{
    private readonly ILogger<RunnerService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly RunnerStoreService _runnerStoreService = runnerStoreService;
    private readonly RunnerTokenStoreService _runnerTokenStoreService = runnerTokenStoreService;

    public async Task<HttpResult<Dictionary<string, RunnerEntity>>> GetAll(CancellationToken cancellationToken = default)
    {
        HttpResult<Dictionary<string, RunnerEntity>> result = new();

        var store = _runnerStoreService.GetStore();

        if (!result.SuccessAndHasValue(await store.GetIds(cancellationToken: cancellationToken), out string[]? runnerIds))
        {
            _logger.LogError("Error runner GetAll: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<RunnerEntity> runnerEntities = [];

        foreach (var id in runnerIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<RunnerEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), out RunnerEntity? runner))
            {
                _logger.LogError("Error runner GetAll: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            runnerEntities.Add(runner);
        }

        result.WithValue(runnerEntities.ToDictionary(i => i.Id));
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<RunnerEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_ID_INVALID", "Runner ID is invalid");
            return result;
        }

        var store = _runnerStoreService.GetStore();

        if (!result.Success(await store.Get<RunnerEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), out RunnerEntity? runner))
        {
            _logger.LogError("Error runner Get: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (runner == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("RUNNER_ID_NOT_FOUND", "Runner ID not found");
            return result;
        }

        result.WithValue(runner);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<RunnerEntity>> Create(RunnerAddDto runnerAddDto, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerEntity> result = new();

        if (string.IsNullOrEmpty(runnerAddDto.TokenId))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_TOKEN_ID_INVALID", "Runner token id is invalid");
            return result;
        }

        if (string.IsNullOrEmpty(runnerAddDto.VagrantBox))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_VAGRANT_BOX_INVALID", "Runner vagrant box is invalid");
            return result;
        }

        if (string.IsNullOrEmpty(runnerAddDto.ProvisionScriptFile))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_PROVISION_SCRIPT_INVALID", "Runner provision script is invalid");
            return result;
        }

        if (runnerAddDto.Labels.Length == 0 && runnerAddDto.Labels.Any(i => i.Contains(' ')))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_LABELS_INVALID", "Runner labels is invalid");
            return result;
        }

        if (runnerAddDto.Replicas > runnerAddDto.MaxReplicas)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_REPLICA_INVALID", "Runner replicas is greater than maxReplicas");
            return result;
        }

        var runnerTokenStore = _runnerTokenStoreService.GetStore();

        if (!result.Success(await runnerTokenStore.Get<RunnerTokenEntity>(runnerAddDto.TokenId.ToLowerInvariant(), cancellationToken: cancellationToken), out RunnerTokenEntity? runnerToken))
        {
            _logger.LogError("Error runner token Get: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (runnerToken == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("RUNNER_TOKEN_ID_NOT_FOUND", "Runner token ID not found");
            return result;
        }

        RunnerEntity newRunner = new()
        {
            TokenId = runnerAddDto.TokenId,
            Id = StringHelpers.Random(6, false).ToLowerInvariant(),
            Rev = StringHelpers.Random(6, false).ToLowerInvariant(),
            Deleted = false,
            VagrantBox = runnerAddDto.VagrantBox,
            ProvisionScriptFile = runnerAddDto.ProvisionScriptFile,
            RunnerOS = runnerAddDto.RunnerOS,
            Replicas = runnerAddDto.Replicas,
            MaxReplicas = runnerAddDto.MaxReplicas,
            Cpus = runnerAddDto.Cpus,
            MemoryGB = runnerAddDto.MemoryGB,
            StorageGB = runnerAddDto.StorageGB,
            Group = runnerAddDto.Group,
            Labels = runnerAddDto.Labels
        };

        var store = _runnerStoreService.GetStore();

        if (!result.Success(await store.Set(newRunner.Id, newRunner, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error runner Create: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newRunner);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Runner id {} was created", newRunner.Id);

        return result;
    }

    public async Task<HttpResult<RunnerEntity>> Edit(string id, RunnerEditDto runnerEditDto, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_ID_INVALID", "Runner ID is invalid");
            return result;
        }

        var store = _runnerStoreService.GetStore();

        if (!result.Success(await store.Get<RunnerEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), false, out RunnerEntity? runner))
        {
            _logger.LogError("Error runner Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (runner == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("RUNNER_ID_NOT_FOUND", "Runner ID not found");
            return result;
        }

        RunnerEntity newRunner = new()
        {
            TokenId = runner.TokenId,
            Id = runner.Id.ToLowerInvariant(),
            Rev = StringHelpers.Random(6, false).ToLowerInvariant(),
            Deleted = false,
            VagrantBox = !string.IsNullOrEmpty(runnerEditDto.NewVagrantBox) ? runnerEditDto.NewVagrantBox : runner.VagrantBox,
            ProvisionScriptFile = !string.IsNullOrEmpty(runnerEditDto.NewProvisionScriptFile) ? runnerEditDto.NewProvisionScriptFile : runner.ProvisionScriptFile,
            RunnerOS = runnerEditDto.NewRunnerOS ?? runner.RunnerOS,
            Replicas = runnerEditDto.NewReplicas ?? runner.Replicas,
            MaxReplicas = runnerEditDto.NewMaxReplicas ?? runner.MaxReplicas,
            Group = !string.IsNullOrEmpty(runnerEditDto.NewGroup) ? runnerEditDto.NewGroup : runner.Group,
            Labels = runnerEditDto.NewLabels ?? runner.Labels,
            Cpus = runnerEditDto.NewCpus ?? runner.Cpus,
            MemoryGB = runnerEditDto.NewMemoryGB ?? runner.MemoryGB,
            StorageGB = runnerEditDto.NewStorageGB ?? runner.StorageGB
        };

        if (newRunner.Labels.Length == 0 && newRunner.Labels.Any(i => i.Contains(' ')))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_LABELS_INVALID", "Runner labels is invalid");
            return result;
        }

        if (newRunner.Replicas > newRunner.MaxReplicas)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_REPLICA_INVALID", "Runner replicas is greater than maxReplicas");
            return result;
        }

        if (!result.Success(await store.Set(id.ToLowerInvariant(), newRunner, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error runner Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newRunner);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Runner id {} was edited", newRunner.Id);

        return result;
    }

    public async Task<HttpResult<RunnerEntity>> Delete(string id, bool hardDelete, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_ID_INVALID", "Runner ID is invalid");
            return result;
        }

        var store = _runnerStoreService.GetStore();

        if (!result.Success(await store.Get<RunnerEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), false, out RunnerEntity? runner))
        {
            _logger.LogError("Error runner Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (runner == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("RUNNER_ID_NOT_FOUND", "Runner ID not found");
            return result;
        }

        if (!hardDelete)
        {
            RunnerEntity newRunner;

            newRunner = new()
            {
                TokenId = runner.TokenId,
                Id = runner.Id,
                Rev = runner.Rev,
                Deleted = true,
                VagrantBox = runner.VagrantBox,
                ProvisionScriptFile = runner.ProvisionScriptFile,
                RunnerOS = runner.RunnerOS,
                Replicas = runner.Replicas,
                MaxReplicas = runner.MaxReplicas,
                Group = runner.Group,
                Labels = runner.Labels,
                Cpus = runner.Cpus,
                MemoryGB = runner.MemoryGB,
                StorageGB = runner.StorageGB
            };

            if (!result.Success(await store.Set(id.ToLowerInvariant(), newRunner, cancellationToken: cancellationToken)))
            {
                _logger.LogError("Error runner Delete: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }

            result.WithValue(newRunner);
            result.WithStatusCode(HttpStatusCode.OK);

            _logger.LogInformation("Runner id {} was mark deleted", newRunner.Id);
        }
        else
        {
            if (!result.Success(await store.Delete(id.ToLowerInvariant(), cancellationToken: cancellationToken)))
            {
                _logger.LogError("Error runner Delete: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }

            result.WithValue(runner);
            result.WithStatusCode(HttpStatusCode.OK);

            _logger.LogInformation("Runner id {} was deleted", id);
        }

        return result;
    }

    public async Task<HttpResult<Dictionary<string, RunnerRuntime>>> GetAllRuntime(CancellationToken cancellationToken = default)
    {
        HttpResult<Dictionary<string, RunnerRuntime>> result = new();

        using var scope = _serviceProvider.CreateScope();
        var runnerRuntimeHolder = scope.ServiceProvider.GetSingletonObjectHolder<Dictionary<string, RunnerRuntime>>();
        var runnerRuntimes = await runnerRuntimeHolder.Get();

        if (runnerRuntimes != null)
        {
            result.WithValue(runnerRuntimes);
        }

        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<RunnerRuntime>> GetRuntime(string id, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerRuntime> result = new();

        using var scope = _serviceProvider.CreateScope();
        var runnerRuntimeHolder = scope.ServiceProvider.GetSingletonObjectHolder<RunnerRuntime[]>();
        var runnerRuntimes = await runnerRuntimeHolder.Get();

        if (runnerRuntimes != null)
        {
            result.WithValue(runnerRuntimes.FirstOrDefault(i => i.RunnerId.Equals(id, StringComparison.InvariantCultureIgnoreCase)));
        }

        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }
}
