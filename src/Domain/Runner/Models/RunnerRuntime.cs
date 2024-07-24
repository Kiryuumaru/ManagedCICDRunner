using Domain.Docker.Models;
using Domain.Runner.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Models;

public class RunnerRuntime
{
    public required string TokenId { get; init; }

    public required string TokenRev { get; init; }

    public required string RunnerId { get; init; }

    public required string RunnerRev { get; init; }

    public required RunnerEntity RunnerEntity { get; init; }

    public Dictionary<string, RunnerInstance> Runners { get; init; } = [];
}
