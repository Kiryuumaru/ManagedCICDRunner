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
    public required string Id { get; init; }

    public required string Rev { get; init; }

    public required RunnerEntity RunnerEntity { get; init; }

    public Dictionary<string, RunnerInstance> Runners { get; init; } = [];
}
