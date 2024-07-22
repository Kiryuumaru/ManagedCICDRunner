using Application.Common;
using Application.Runner.Services;
using CliWrap.EventStream;
using Domain.Docker.Enums;
using Domain.Docker.Models;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;
using static System.Net.Mime.MediaTypeNames;

namespace Application.Docker.Services;

public class DockerService(ILogger<DockerService> logger)
{
    private readonly ILogger<DockerService> _logger = logger;

    public async Task<DockerContainer[]> GetContainers(RunnerOSType runnerOS)
    {
        List<DockerContainer> dockerContainers = [];

        string dockerCmd = GetDockerCommand(runnerOS);

        await foreach (var commandEvent in Cli.RunListen($"{dockerCmd} ps --all --no-trunc --format \"{{{{json .}}}}\""))
        {
            string line = "";
            switch (commandEvent)
            {
                case StandardOutputCommandEvent outEvent:
                    _logger.LogDebug("{x}", outEvent.Text);
                    line = outEvent.Text;
                    break;
                case StandardErrorCommandEvent errEvent:
                    _logger.LogDebug("{x}", errEvent.Text);
                    line = errEvent.Text;
                    break;
            }
            if (!string.IsNullOrEmpty(line))
            {
                var containerRaw = JsonSerializer.Deserialize<Dictionary<string, string>>(line)!;
                ContainerState state = containerRaw["State"] switch
                {
                    "created" => ContainerState.Created,
                    "running" => ContainerState.Running,
                    "restarting" => ContainerState.Restarting,
                    "paused" => ContainerState.Paused,
                    "exited" => ContainerState.Exited,
                    "removing" => ContainerState.Removing,
                    "dead" => ContainerState.Dead,
                    _ => throw new Exception($"{containerRaw["State"]} container state not supported")
                };
                Dictionary<string, string> labels = [];
                foreach (var label in containerRaw["Labels"].Split(","))
                {
                    var labelSplit = label.Split("=");
                    if (labelSplit.Length == 2)
                    {
                        labels[labelSplit[0]] = labelSplit[1];
                    }
                }
                dockerContainers.Add(new()
                {
                    Id = containerRaw["ID"],
                    Name = containerRaw["Names"],
                    Image = containerRaw["Image"],
                    State = state,
                    Labels = labels,
                });
            }
        }

        return [.. dockerContainers];
    }

    public async Task DeleteContainer(RunnerOSType runnerOS, params string[] ids)
    {
        string dockerCmd = GetDockerCommand(runnerOS);

        foreach (var id in ids)
        {
            try
            {
                await foreach (var commandEvent in Cli.RunListen($"{dockerCmd} kill {id}"))
                {
                    switch (commandEvent)
                    {
                        case StandardOutputCommandEvent outEvent:
                            _logger.LogDebug("{x}", outEvent.Text);
                            break;
                        case StandardErrorCommandEvent errEvent:
                            _logger.LogDebug("{x}", errEvent.Text);
                            break;
                    }
                }
            }
            catch { }
            try
            {
                await foreach (var commandEvent in Cli.RunListen($"{dockerCmd} rm --force {id}"))
                {
                    switch (commandEvent)
                    {
                        case StandardOutputCommandEvent outEvent:
                            _logger.LogDebug("{x}", outEvent.Text);
                            break;
                        case StandardErrorCommandEvent errEvent:
                            _logger.LogDebug("{x}", errEvent.Text);
                            break;
                    }
                }
            }
            catch { }
        }
    }

    public async Task Build(RunnerOSType runnerOS, string image, string runnerId)
    {
        string? localDockerfile = GetLocalDockerfilePath(runnerOS, image);

        string dockerCmd = GetDockerCommand(runnerOS);

        string prepareCmd;
        string actualImage;
        if (localDockerfile != null)
        {
            actualImage = $"{runnerId}:latest".ToLowerInvariant();
            prepareCmd = $"{dockerCmd} build -t {actualImage} -f \"{localDockerfile}\" .";
        }
        else
        {
            actualImage = image;
            prepareCmd = $"{dockerCmd} pull {actualImage}";
        }

        await Cli.RunListenAndLog(_logger, prepareCmd);
    }

    public async Task Run(RunnerOSType runnerOS, string name, string image, string runnerId, Dictionary<string, string> labels, int cpus, int memoryGB, string input, Dictionary<string, string> envVars)
    {
        string? localDockerfile = GetLocalDockerfilePath(runnerOS, image);

        string dockerCmd = GetDockerCommand(runnerOS);
        string dockerEntrypoint = GetDockerEntrypoint(runnerOS);

        string actualImage;
        if (localDockerfile != null)
        {
            actualImage = $"{runnerId}:latest".ToLowerInvariant();
        }
        else
        {
            actualImage = image;
        }

        string runCmd = $"{dockerCmd} run --name \"{name}\" --cpus=\"{cpus}\" --memory=\"{memoryGB}g\" -d --rm --entrypoint {dockerEntrypoint}";

        foreach (var label in labels)
        {
            runCmd += $" -l \"{label.Key}={label.Value}\"";
        }

        foreach (var envVar in envVars)
        {
            runCmd += $" -e \"{envVar.Key}={envVar.Value}\"";
        }

        runCmd += $" {actualImage} -c \"{input}\"";

        await Cli.RunListenAndLog(_logger, runCmd, null);
    }

    private static string GetDockerCommand(RunnerOSType runnerOS)
    {
        return runnerOS switch
        {
            RunnerOSType.Linux => "wsl --exec docker",
            RunnerOSType.Windows => "docker",
            _ => throw new NotSupportedException()
        };
    }

    private static string GetDockerEntrypoint(RunnerOSType runnerOS)
    {
        return runnerOS switch
        {
            RunnerOSType.Linux => "/bin/bash",
            RunnerOSType.Windows => "powershell",
            _ => throw new NotSupportedException()
        };
    }

    private static string? GetLocalDockerfilePath(RunnerOSType runnerOS, string image)
    {
        string? localDockerfile = null;
        if (Path.IsPathRooted(image) && AbsolutePath.Parse(image).FileExists())
        {
            localDockerfile = AbsolutePath.Parse(image);
        }
        else if ((AbsolutePath.Parse(Environment.CurrentDirectory) / image).FileExists())
        {
            localDockerfile = AbsolutePath.Parse(Environment.CurrentDirectory) / image;
        }
        if (localDockerfile != null)
        {
            if (runnerOS == RunnerOSType.Linux)
            {
                localDockerfile = localDockerfile.Replace("\\", "/");
                var localDockerfileSplit = localDockerfile.Split(':');
                if (localDockerfileSplit.Length > 1)
                {
                    localDockerfile = $"/mnt/{localDockerfileSplit[0].ToLower()}/{localDockerfileSplit[1]}";
                }
            }
            else if (runnerOS == RunnerOSType.Windows)
            {
                localDockerfile = localDockerfile.Replace("/", "\\");
            }
            else
            {
                throw new NotSupportedException();
            }
        }
        return localDockerfile;
    }
}
