using Domain.Docker.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Docker.Models;

public class DockerContainer
{
    public required string Id { get; init; }

    public required string Image { get; init; }

    public required string Name { get; init; }

    public required ContainerState State { get; init; }

    public required Dictionary<string, string> Labels { get; init; }
}
