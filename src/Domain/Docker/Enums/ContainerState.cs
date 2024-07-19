using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Docker.Enums;

public enum ContainerState
{
    Created, Running, Restarting, Exited, Paused, Dead, Removing
}
