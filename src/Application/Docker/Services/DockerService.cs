﻿using Application.Common;
using Application.Runner.Services;
using CliWrap.EventStream;
using Domain.Docker.Enums;
using Domain.Docker.Models;
using Domain.Runner.Entities;
using Domain.Runner.Enums;
using Domain.Runner.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.Docker.Services;

public class DockerService(ILogger<DockerService> logger)
{
    private readonly ILogger<DockerService> _logger = logger;

    public async Task<bool> IsDaemonAlive()
    {
        return
            (await IsDaemonAlive(RunnerOSType.Windows)) &&
            (await IsDaemonAlive(RunnerOSType.Linux));
    }

    public async Task<bool> IsDaemonAlive(RunnerOSType runnerOS)
    {
        string dockerCmd = GetDockerCommand(runnerOS);

        bool isAlive = true;

        await foreach (var commandEvent in Cli.RunListen($"{dockerCmd} info"))
        {
            switch (commandEvent)
            {
                case StandardOutputCommandEvent outEvent:
                    _logger.LogTrace("{x}", outEvent.Text);
                    break;
                case StandardErrorCommandEvent errEvent:
                    _logger.LogTrace("{x}", errEvent.Text);
                    break;
                case ExitedCommandEvent exitEvent:
                    if (exitEvent.ExitCode != 0)
                    {
                        isAlive = false;
                    }
                    break;
            }
        }

        return isAlive;
    }

    public async Task<DockerContainer[]> GetContainers()
    {
        List<DockerContainer> dockerContainers = [];

        dockerContainers.AddRange(await GetContainers(RunnerOSType.Linux));
        dockerContainers.AddRange(await GetContainers(RunnerOSType.Windows));

        return [.. dockerContainers];
    }

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

    public async Task<DockerImage[]> GetImages()
    {
        List<DockerImage> dockerImages = [];

        dockerImages.AddRange(await GetImages(RunnerOSType.Linux));
        dockerImages.AddRange(await GetImages(RunnerOSType.Windows));

        return [.. dockerImages];
    }

    public async Task<DockerImage[]> GetImages(RunnerOSType runnerOS)
    {
        List<DockerImage> dockerImages = [];

        string dockerCmd = GetDockerCommand(runnerOS);

        await foreach (var commandEvent in Cli.RunListen($"{dockerCmd} image ls --all --no-trunc --format \"{{{{json .}}}}\""))
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
                dockerImages.Add(new()
                {
                    Repository = containerRaw["Repository"],
                    Tag = containerRaw["Tag"]
                });
            }
        }

        return [.. dockerImages];
    }

    public async Task DeleteImages(RunnerOSType runnerOS, params string[] images)
    {
        string dockerCmd = GetDockerCommand(runnerOS);

        foreach (var image in images)
        {
            try
            {
                await foreach (var commandEvent in Cli.RunListen($"{dockerCmd} rmi --force {image}"))
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

    public async Task<string> Build(RunnerOSType runnerOS, string image, string imageName)
    {
        string? localDockerfile = GetLocalDockerfilePath(image);

        string dockerCmd = GetDockerCommand(runnerOS);

        string prepareCmd;
        string actualImage;
        if (localDockerfile != null)
        {
            actualImage = imageName.ToLowerInvariant();
            prepareCmd = $"{dockerCmd} build -t {actualImage} -f \"{localDockerfile}\" .";
        }
        else
        {
            actualImage = image;
            prepareCmd = $"{dockerCmd} pull {actualImage}";
        }

        await Cli.RunListenAndLog(_logger, prepareCmd);

        return actualImage;
    }

    public async Task Run(RunnerOSType runnerOS, string name, string image, string runnerId, int cpus, int memoryGB, string? input, string? args)
    {
        string? localDockerfile = GetLocalDockerfilePath(image);

        string dockerCmd = GetDockerCommand(runnerOS);

        string actualImage;
        if (localDockerfile != null)
        {
            actualImage = $"{runnerId}:latest".ToLowerInvariant();
        }
        else
        {
            actualImage = image;
        }

        string runCmd = $"{dockerCmd} run --name \"{name}\" --cpus=\"{cpus}\" --memory=\"{memoryGB}g\" -d --rm";

        if (!string.IsNullOrEmpty(input))
        {
            runCmd += $" --entrypoint {GetDockerEntrypoint(runnerOS)}";
        }

        if (!string.IsNullOrEmpty(args))
        {
            runCmd += $" {args}";
        }

        runCmd += $" {actualImage}";

        if (!string.IsNullOrEmpty(input))
        {
            runCmd += $" -c \"{input}\"";
        }

        await Cli.RunListenAndLog(_logger, runCmd);
    }

    private static string GetDockerCommand(RunnerOSType runnerOS)
    {
        return runnerOS switch
        {
            RunnerOSType.Linux => "docker -H \"npipe:////./pipe/docker_engine_linux\"",
            RunnerOSType.Windows => "docker -H \"npipe:////./pipe/docker_engine_windows\"",
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

    private static string? GetLocalDockerfilePath(string image)
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
            localDockerfile = localDockerfile.Replace("/", "\\");
        }
        return localDockerfile;
    }
}
