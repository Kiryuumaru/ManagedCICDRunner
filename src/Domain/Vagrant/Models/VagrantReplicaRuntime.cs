using Domain.Vagrant.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Vagrant.Models;

public class VagrantReplicaRuntime : VagrantReplica
{
    public required VagrantReplicaState State { get; init; }
}
