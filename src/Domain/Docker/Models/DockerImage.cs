using Domain.Docker.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Docker.Models;

public class DockerImage
{
    public required string Repository { get; init; }

    public required string Tag { get; init; }
}
