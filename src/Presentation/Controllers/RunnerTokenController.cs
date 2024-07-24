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
public class RunnerTokenController(RunnerTokenService runnerTokenService) : ControllerBase
{
    private readonly RunnerTokenService _runnerTokenService = runnerTokenService;

    /// <summary>
    /// Creates a new runner token entity.
    /// </summary>
    /// <param name="runnerToken">The data for the new runner token entity.</param>
    /// <returns>An HTTP result containing the created RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided data is invalid.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPost("api/runner-token")]
    public Task<HttpResult<RunnerTokenEntity>> Create([FromBody] RunnerTokenAddDto runnerToken)
    {
        return _runnerTokenService.Create(runnerToken);
    }

    /// <summary>
    /// Updates an existing runner token entity.
    /// </summary>
    /// <param name="id">The ID of the runner token entity to update.</param>
    /// <param name="runnerToken">The updated data for the runner token entity.</param>
    /// <returns>An HTTP result containing the updated RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID or data is invalid.</response>
    /// <response code="404">Returns when the Runner entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPut("api/runner-token/{id}")]
    public Task<HttpResult<RunnerTokenEntity>> Edit(string id, [FromBody] RunnerTokenEditDto runnerToken)
    {
        return _runnerTokenService.Edit(id, runnerToken);
    }

    /// <summary>
    /// Deletes a specific runner token entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the runner token entity to delete.</param>
    /// <returns>An HTTP result indicating the success of the operation.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the Runner entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpDelete("api/runner-token/{id}")]
    public Task<HttpResult<RunnerTokenEntity>> Delete(string id)
    {
        return _runnerTokenService.Delete(id, false);
    }

    /// <summary>
    /// Retrieves all runnertoken  entities.
    /// </summary>
    /// <returns>An HTTP result containing an array of RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("api/runner-token")]
    public Task<HttpResult<RunnerTokenEntity[]>> GetAll()
    {
        return _runnerTokenService.GetAll();
    }

    /// <summary>
    /// Retrieves a specific runner token entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the runner token entity to retrieve.</param>
    /// <returns>An HTTP result containing the RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("api/runner-token/{id}")]
    public Task<HttpResult<RunnerTokenEntity>> Get(string id)
    {
        return _runnerTokenService.Get(id);
    }
}
