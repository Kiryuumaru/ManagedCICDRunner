using Domain.Vagrant.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Vagrant.Models;

public class VagrantReplica
{
    public required string BuildId { get; init; }

    public required string Id { get; init; }

    public required string Rev { get; init; }

    public required int Cpus { get; init; }

    public required int MemoryGB { get; init; }

    public required int StorageGB { get; init; }

    public required string? VMName { get; init; }

    public required Dictionary<string, string> Labels { get; init; }
}
