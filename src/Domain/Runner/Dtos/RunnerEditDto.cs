using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Dtos;

public class RunnerEditDto
{
    public string? NewVagrantBox { get; init; }

    public string? NewProvisionScriptFile { get; init; }

    public RunnerOSType? NewRunnerOS { get; init; }

    public int? NewReplicas { get; init; }

    public int? NewMaxReplicas { get; init; }

    public int? NewCpus { get; init; }

    public int? NewMemoryGB { get; init; }

    public string? NewGroup { get; init; }

    public string[]? NewLabels { get; init; }
}
