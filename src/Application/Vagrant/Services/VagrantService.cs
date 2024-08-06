using Application.Common;
using Application.Runner.Services;
using CliWrap.EventStream;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Runner.Models;
using Domain.Vagrant.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.Vagrant.Services;

public class VagrantService(ILogger<VagrantService> logger)
{
    private readonly ILogger<VagrantService> _logger = logger;

    public async Task<VagrantInstance[]> GetInstances()
    {
        List<VagrantInstance> vagrantInstances = [];


        return [.. vagrantInstances];
    }

    public async Task DeleteInstance(params string[] ids)
    {
    }

    public async Task<VagrantBox[]> GetBoxes()
    {
        List<VagrantBox> vagrantBoxes = [];

        return [.. vagrantBoxes];
    }

    public async Task DeleteBox(params string[] images)
    {

    }

    public async Task Run(string name, string image, string runnerId, int cpus, int memoryGB, string? input, string? args)
    {

    }
}
