﻿using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Dtos;

public class RunnerAddDto
{
    public required string TokenId { get; init; }

    public required string Image { get; init; }

    public required RunnerOSType RunnerOS { get; init; }

    public required int Count { get; init; }

    public required int Cpus { get; init; }

    public required int MemoryGB { get; init; }

    public string? Group { get; init; }

    public required string[] Labels { get; init; }
}
