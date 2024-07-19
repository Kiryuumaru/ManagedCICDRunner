using Application.Runner.Services;
using Domain.Runner.Dtos;
using Domain.Runner.Entities;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;

namespace Presentation.Controllers;

/// <summary>
/// Controller for managing Runner entities.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class RunnerController(RunnerApiService runnerApiService) : ControllerBase
{
    private readonly RunnerApiService _runnerApiService = runnerApiService;

    /// <summary>
    /// Retrieves all Runner entities.
    /// </summary>
    /// <returns>An HTTP result containing an array of RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet]
    public Task<HttpResult<RunnerEntity[]>> GetAll()
    {
        return _runnerApiService.GetAll();
    }

    /// <summary>
    /// Retrieves a specific Runner entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the Runner entity to retrieve.</param>
    /// <returns>An HTTP result containing the RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the provided ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpGet("{id}")]
    public Task<HttpResult<RunnerEntity>> Get(string id)
    {
        return _runnerApiService.Get(id);
    }

    /// <summary>
    /// Creates a new Runner entity.
    /// </summary>
    /// <param name="runner">The data for the new Runner entity.</param>
    /// <returns>An HTTP result containing the created RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided data is invalid.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPost]
    public Task<HttpResult<RunnerEntity>> Create([FromBody] RunnerAddDto runner)
    {
        return _runnerApiService.Create(runner);
    }

    /// <summary>
    /// Updates an existing Runner entity.
    /// </summary>
    /// <param name="id">The ID of the Runner entity to update.</param>
    /// <param name="runner">The updated data for the Runner entity.</param>
    /// <returns>An HTTP result containing the updated RunnerTokenEntity.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID or data is invalid.</response>
    /// <response code="404">Returns when the Runner entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpPut("{id}")]
    public Task<HttpResult<RunnerEntity>> Edit(string id, [FromBody] RunnerEditDto runner)
    {
        return _runnerApiService.Edit(id, runner);
    }

    /// <summary>
    /// Deletes a specific Runner entity by its ID.
    /// </summary>
    /// <param name="id">The ID of the Runner entity to delete.</param>
    /// <returns>An HTTP result indicating the success of the operation.</returns>
    /// <response code="200">Returns when the operation is successful.</response>
    /// <response code="400">Returns when the provided ID is invalid.</response>
    /// <response code="404">Returns when the Runner entity with the given ID is not found.</response>
    /// <response code="500">Returns when an unexpected error occurs.</response>
    [HttpDelete("{id}")]
    public Task<HttpResult<RunnerEntity>> Delete(string id)
    {
        return _runnerApiService.Delete(id, false);
    }
}
