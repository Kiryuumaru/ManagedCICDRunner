using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Vagrant.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Models;

public class RunnerInstance
{
    public required string Name { get; init; }

    public required RunnerAction? RunnerAction { get; init; }

    public required VagrantReplicaRuntime? VagrantReplica { get; init; }

    public required RunnerStatus Status { get; init; }
}
