using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Dtos;

public class RunnerEditDto
{
    public string? NewImage { get; init; }

    public RunnerOSType? NewRunnerOS { get; init; }

    public int? NewCount { get; init; }

    public int? NewCpus { get; init; }

    public int? NewMemoryGB { get; init; }

    public string? NewGroup { get; init; }

    public string[]? NewLabels { get; init; }

    public string? NewGithubToken { get; init; }

    public string? NewGithubRepo { get; init; }

    public string? NewGithubOrg { get; init; }
}
