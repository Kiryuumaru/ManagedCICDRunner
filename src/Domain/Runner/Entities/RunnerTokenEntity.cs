using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Entities;

public class RunnerTokenEntity
{
    public required string Id { get; init; }

    public required string Rev { get; init; }

    public required bool Deleted { get; init; }

    public required string GithubToken { get; init; }

    public required string? GithubRepo { get; init; }

    public required string? GithubOrg { get; init; }
}
