using Application.Runner.Services;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
using Domain.Runner.Models;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;

namespace Presentation.Controllers;

/// <summary>
/// Controller for managing Runner entities.
/// </summary>
[ApiController]
public class RunnerController(RunnerService runnerService) : ControllerBase
{
    private readonly RunnerService _runnerService = runnerService;

    /// <summary>
    /// Creates a new runner entity.
    /// </summary>
    /// <param name="runner">The data for the new runner entity.</param>
    /// <returns>An HTTP result containing the created RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided data is invalid.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPost("api/runner")]
    public Task<HttpResult<RunnerEntity>> Create([FromBody] RunnerAddDto runner)
    {
        return _runnerService.Create(runner);
    }

    /// <summary>
    /// Updates an existing runner entity.
    /// </summary>
    /// <param name="id">The ID of the runner entity to update.</param>
    /// <param name="runner">The updated data for the runner entity.</param>
    /// <returns>An HTTP result containing the updated RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID or data is invalid.</response>
    /// <response code="404">Returns when the runner entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPut("api/runner/{id}")]
    public Task<HttpResult<RunnerEntity>> Edit(string id, [FromBody] RunnerEditDto runner)
    {
        return _runnerService.Edit(id, runner);
    }

    /// <summary>
    /// Deletes a specific runner entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the runner entity to delete.</param>
    /// <returns>An HTTP result indicating the success of the operation.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the runner entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpDelete("api/runner/{id}")]
    public Task<HttpResult<RunnerEntity>> Delete(string id)
    {
        return _runnerService.Delete(id, false);
    }

    /// <summary>
    /// Retrieves all Runner entities.
    /// </summary>
    /// <returns>An HTTP result containing an array of RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("api/runner")]
    public Task<HttpResult<Dictionary<string, RunnerEntity>>> GetAll()
    {
        return _runnerService.GetAll();
    }

    /// <summary>
    /// Retrieves a specific runner entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the runner entity to retrieve.</param>
    /// <returns>An HTTP result containing the RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("api/runner/{id}")]
    public Task<HttpResult<RunnerEntity>> Get(string id)
    {
        return _runnerService.Get(id);
    }
}
