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

public class RunnerTokenService(ILogger<RunnerTokenService> logger, IServiceProvider serviceProvider, RunnerTokenStoreService runnerTokenStoreService) : IRunnerTokenService
{
    private readonly ILogger<RunnerTokenService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly RunnerTokenStoreService _runnerTokenStoreService = runnerTokenStoreService;

    public async Task<HttpResult<Dictionary<string, RunnerTokenEntity>>> GetAll(CancellationToken cancellationToken = default)
    {
        HttpResult<Dictionary<string, RunnerTokenEntity>> result = new();

        var store = _runnerTokenStoreService.GetStore();

        if (!result.SuccessAndHasValue(await store.GetIds(cancellationToken: cancellationToken), out string[]? runnerIds))
        {
            _logger.LogError("Error runner token GetAll: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<RunnerTokenEntity> runnerTokenEntities = [];

        foreach (var id in runnerIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<RunnerTokenEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), out RunnerTokenEntity? runnerToken))
            {
                _logger.LogError("Error runner token GetAll: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            runnerTokenEntities.Add(runnerToken);
        }

        result.WithValue(runnerTokenEntities.ToDictionary(i => i.Id));
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<RunnerTokenEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerTokenEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_TOKEN_ID_INVALID", "Runner token ID is invalid");
            return result;
        }

        var store = _runnerTokenStoreService.GetStore();

        if (!result.Success(await store.Get<RunnerTokenEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), out RunnerTokenEntity? runnerToken))
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

        result.WithValue(runnerToken);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<RunnerTokenEntity>> Create(RunnerTokenAddDto runnerTokenAddDto, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerTokenEntity> result = new();

        if (string.IsNullOrEmpty(runnerTokenAddDto.GithubToken))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_TOKEN_TOKEN_INVALID", "Runner token token is invalid");
            return result;
        }

        if (string.IsNullOrEmpty(runnerTokenAddDto.GithubOrg) && string.IsNullOrEmpty(runnerTokenAddDto.GithubRepo))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_TOKEN_ORG_REPO_INVALID", "Runner token org or repo is invalid");
            return result;
        }

        RunnerTokenEntity newRunnerToken = new()
        {
            Id = StringHelpers.Random(6, false).ToLowerInvariant(),
            Rev = StringHelpers.Random(6, false).ToLowerInvariant(),
            Deleted = false,
            GithubToken = runnerTokenAddDto.GithubToken,
            GithubOrg = runnerTokenAddDto.GithubOrg,
            GithubRepo = runnerTokenAddDto.GithubRepo,
        };

        var store = _runnerTokenStoreService.GetStore();

        if (!result.Success(await store.Set(newRunnerToken.Id, newRunnerToken, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error runner token Create: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newRunnerToken);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Runner token id {} was created", newRunnerToken.Id);

        return result;
    }

    public async Task<HttpResult<RunnerTokenEntity>> Edit(string id, RunnerTokenEditDto runnerTokenEditDto, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerTokenEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_TOKEN_ID_INVALID", "Runner token ID is invalid");
            return result;
        }

        var store = _runnerTokenStoreService.GetStore();

        if (!result.Success(await store.Get<RunnerTokenEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), false, out RunnerTokenEntity? runnerToken))
        {
            _logger.LogError("Error runner token Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (runnerToken == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("RUNNER_TOKEN_ID_NOT_FOUND", "Runner token ID not found");
            return result;
        }

        RunnerTokenEntity newRunnerToken = new()
        {
            Id = runnerToken.Id.ToLowerInvariant(),
            Rev = StringHelpers.Random(6, false).ToLowerInvariant(),
            Deleted = false,
            GithubToken = !string.IsNullOrEmpty(runnerTokenEditDto.NewGithubToken) ? runnerTokenEditDto.NewGithubToken : runnerToken.GithubToken,
            GithubOrg = !string.IsNullOrEmpty(runnerTokenEditDto.NewGithubOrg) ? runnerTokenEditDto.NewGithubOrg : runnerToken.GithubOrg,
            GithubRepo = !string.IsNullOrEmpty(runnerTokenEditDto.NewGithubRepo) ? runnerTokenEditDto.NewGithubRepo : runnerToken.GithubRepo
        };

        if (!result.Success(await store.Set(id.ToLowerInvariant(), newRunnerToken, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error runner token Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newRunnerToken);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Runner token id {} was edited", newRunnerToken.Id);

        return result;
    }

    public async Task<HttpResult<RunnerTokenEntity>> Delete(string id, bool hardDelete, CancellationToken cancellationToken = default)
    {
        HttpResult<RunnerTokenEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("RUNNER_TOKEN_ID_INVALID", "Runner token ID is invalid");
            return result;
        }

        var store = _runnerTokenStoreService.GetStore();

        if (!result.Success(await store.Get<RunnerTokenEntity>(id.ToLowerInvariant(), cancellationToken: cancellationToken), false, out RunnerTokenEntity? runnerToken))
        {
            _logger.LogError("Error runner token Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (runnerToken == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("RUNNER_TOKEN_ID_NOT_FOUND", "Runner token ID not found");
            return result;
        }

        if (!hardDelete)
        {
            RunnerTokenEntity newRunnerToken = new()
            {
                Id = runnerToken.Id,
                Rev = runnerToken.Rev,
                Deleted = true,
                GithubToken = runnerToken.GithubToken,
                GithubOrg = runnerToken.GithubOrg,
                GithubRepo = runnerToken.GithubRepo
            };

            if (!result.Success(await store.Set(id.ToLowerInvariant(), newRunnerToken, cancellationToken: cancellationToken)))
            {
                _logger.LogError("Error runner Delete: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }

            result.WithValue(newRunnerToken);
            result.WithStatusCode(HttpStatusCode.OK);

            _logger.LogInformation("Runner token id {} was mark deleted", newRunnerToken.Id);
        }
        else
        {
            if (!result.Success(await store.Delete(id.ToLowerInvariant(), cancellationToken: cancellationToken)))
            {
                _logger.LogError("Error runner token Delete: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }

            result.WithValue(runnerToken);
            result.WithStatusCode(HttpStatusCode.OK);

            _logger.LogInformation("Runner token id {} was deleted", id);
        }

        return result;
    }
}
