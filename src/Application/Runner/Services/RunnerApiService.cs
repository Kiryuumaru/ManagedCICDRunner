using Application.Common;
using Application.Runner.Interfaces;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Runner.Services;

public class RunnerApiService(IServiceProvider serviceProvider, IConfiguration configuration) : IRunnerService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    private Task<HttpResult<TReturn>> InvokeEndpoint<TReturn>(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var endpoint = _configuration.GetVarRefValue("SERVER_ENDPOINT").Trim('/') + "/api/runner" + path;
        return new HttpClient().Execute<TReturn>(method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    private Task<HttpResult<TReturn>> InvokeEndpoint<TPayload, TReturn>(HttpMethod method, TPayload payload, string path, CancellationToken cancellationToken)
    {
        var endpoint = _configuration.GetVarRefValue("SERVER_ENDPOINT").Trim('/') + "/api/runner" + path;
        return new HttpClient().ExecuteWithContent<TReturn, TPayload>(payload, method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    public Task<HttpResult<RunnerEntity>> Create(RunnerAddDto runnerAddDto, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<RunnerAddDto, RunnerEntity>(HttpMethod.Post, runnerAddDto, "", cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<RunnerService>().Create(runnerAddDto, cancellationToken);
        }
    }

    public Task<HttpResult<RunnerEntity>> Delete(string id, bool hardDelete, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<RunnerEntity>(HttpMethod.Delete, "/" + id + $"?hardDelete={hardDelete}", cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<RunnerService>().Delete(id, hardDelete, cancellationToken);
        }
    }

    public Task<HttpResult<RunnerEntity>> Edit(string id, RunnerEditDto runnerEditDto, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<RunnerEditDto, RunnerEntity>(HttpMethod.Put, runnerEditDto, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<RunnerService>().Edit(id, runnerEditDto, cancellationToken);
        }
    }

    public Task<HttpResult<RunnerEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<RunnerEntity>(HttpMethod.Get, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<RunnerService>().Get(id, cancellationToken);
        }
    }

    public Task<HttpResult<RunnerEntity[]>> GetAll(CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<RunnerEntity[]>(HttpMethod.Get, "", cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<RunnerService>().GetAll(cancellationToken);
        }
    }
}
