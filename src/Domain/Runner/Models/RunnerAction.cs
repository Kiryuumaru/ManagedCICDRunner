﻿using Domain.Runner.Entities;
using Domain.Runner.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Runner.Models;

public class RunnerAction
{
    public required string Name { get; init; }

    public required string RunnerId { get; init; }

    public required string Id { get; init; }

    public required RunnerActionStatus Status { get; init; }
}
