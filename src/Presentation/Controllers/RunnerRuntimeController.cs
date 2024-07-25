using Application.Runner.Services;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
using Domain.Runner.Models;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;

namespace Presentation.Controllers;

/// <summary>
/// Controller for managing runner runtimes.
/// </summary>
[ApiController]
public class RunnerRuntimeController(RunnerService runnerService) : ControllerBase
{
    private readonly RunnerService _runnerService = runnerService;

    /// <summary>
    /// Retrieves all runner runtime entities.
    /// </summary>
    /// <returns>An HTTP result containing an array of RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("api/runner-runtime")]
    public Task<HttpResult<Dictionary<string, RunnerRuntime>>> GetAll()
    {
        return _runnerService.GetAllRuntime();
    }

    /// <summary>
    /// Retrieves a specific runner runtime entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the runner entity to retrieve.</param>
    /// <returns>An HTTP result containing the RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("api/runner-runtime/{id}")]
    public Task<HttpResult<RunnerRuntime>> Get(string id)
    {
        return _runnerService.GetRuntime(id);
    }
}
