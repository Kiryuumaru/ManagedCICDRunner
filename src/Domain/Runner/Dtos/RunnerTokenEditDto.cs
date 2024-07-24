using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Dtos;

public class RunnerTokenEditDto
{
    public string? NewGithubToken { get; init; }

    public string? NewGithubRepo { get; init; }

    public string? NewGithubOrg { get; init; }
}
