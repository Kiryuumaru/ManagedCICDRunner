using Application.Common;
using Application.LocalStore.Services;
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

namespace Application.Runner.Interfaces;

public interface IRunnerTokenService
{
    Task<HttpResult<Dictionary<string, RunnerTokenEntity>>> GetAll(CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerTokenEntity>> Get(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerTokenEntity>> Create(RunnerTokenAddDto runnerTokenAddDto, CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerTokenEntity>> Edit(string id, RunnerTokenEditDto runnerTokenEditDto, CancellationToken cancellationToken = default);

    Task<HttpResult<RunnerTokenEntity>> Delete(string id, bool hardDelete, CancellationToken cancellationToken = default);
}
