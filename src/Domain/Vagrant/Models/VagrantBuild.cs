using Domain.Runner.Enums;
using Domain.Vagrant.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Vagrant.Models;

public class VagrantBuild
{
    public required string Id { get; init; }

    public required string VagrantFileHash { get; init; }

    public required RunnerOSType RunnerOS { get; init; }

    public required string Rev { get; init; }
}
