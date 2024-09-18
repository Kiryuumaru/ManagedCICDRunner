using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Entities;

public class RunnerEntity
{
    public required string TokenId { get; init; }

    public required string Id { get; init; }

    public required string Rev { get; init; }

    public required bool Deleted { get; init; }

    public required string VagrantBox { get; init; }

    public required string ProvisionScriptFile { get; init; }

    public required RunnerOSType RunnerOS { get; init; }

    public required int Replicas { get; init; }

    public required int MaxReplicas { get; init; }

    public required int Cpus { get; init; }

    public required int MemoryGB { get; init; }

    public required int StorageGB { get; init; }

    public required string? Group { get; init; }

    public required string[] Labels { get; init; }
}
