using Domain.Vagrant.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Vagrant.Models;

public class VagrantInstance
{
    public required string Id { get; init; }

    public required string Image { get; init; }

    public required string Name { get; init; }

    public required InstanceState State { get; init; }

    public required Dictionary<string, string> Labels { get; init; }
}
