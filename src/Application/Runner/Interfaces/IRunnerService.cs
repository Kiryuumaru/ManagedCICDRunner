using Application.Common;
using Application.LocalStore.Services;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
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

namespace Application.Runner.Interfaces;

public interface IRunnerService
{
    Task<HttpResult<RunnerEntity[]>> GetAll(CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerEntity>> Get(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerEntity>> Create(RunnerAddDto runnerAddDto, CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerEntity>> Edit(string id, RunnerEditDto runnerEditDto, CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerEntity>> Delete(string id, bool hardDelete, CancellationToken cancellationToken = default);
}
