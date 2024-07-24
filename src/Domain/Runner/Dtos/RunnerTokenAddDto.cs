using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Dtos;

public class RunnerTokenAddDto
{
    public required string GithubToken { get; init; }

    public string? GithubRepo { get; init; }

    public string? GithubOrg { get; init; }
}
