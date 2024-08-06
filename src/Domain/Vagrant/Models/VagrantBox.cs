using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Vagrant.Models;

public class VagrantBox
{
    public required string Repository { get; init; }

    public required string Tag { get; init; }
}
