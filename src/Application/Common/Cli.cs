using CliWrap;
using CliWrap.EventStream;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

public static class Cli
{
    public static Command BuildRun(
        string path,
        string[] args,
        IDictionary<string, string?>? environmentVariables = default,
        PipeSource? inPipeTarget = default,
        PipeTarget? outPipeTarget = default,
        PipeTarget? errPipeTarget = default)
    {
        Command osCli = CliWrap.Cli.Wrap(path)
            .WithArguments(args, false)
            .WithValidation(CommandResultValidation.None)
            .WithEnvironmentVariables(environmentVariables?.ToDictionary() ?? []);

        if (inPipeTarget != null)
        {
            osCli = osCli
                .WithStandardInputPipe(inPipeTarget);
        }

        if (outPipeTarget != null)
        {
            osCli = osCli
                .WithStandardOutputPipe(outPipeTarget);
        }

        if (errPipeTarget != null)
        {
            osCli = osCli
                .WithStandardErrorPipe(errPipeTarget);
        }

        return osCli;
    }

    public static Command BuildRun(
        string command,
        IDictionary<string, string?>? environmentVariables = default,
        PipeSource? inPipeTarget = default,
        PipeTarget? outPipeTarget = default,
        PipeTarget? errPipeTarget = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return BuildRun("cmd", ["/c", $"\"{command}\""], environmentVariables, inPipeTarget, outPipeTarget, errPipeTarget);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return BuildRun("/bin/bash", ["-c", $"\"{command}\""], environmentVariables, inPipeTarget, outPipeTarget, errPipeTarget);
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    public static async Task<string> RunOnce(
        string path,
        string[] args,
        IDictionary<string, string?>? environmentVariables = default,
        CancellationToken stoppingToken = default)
    {
        var stdBuffer = new StringBuilder();

        await BuildRun(path, args, environmentVariables, null, PipeTarget.ToStringBuilder(stdBuffer), PipeTarget.ToStringBuilder(stdBuffer))
            .ExecuteAsync(stoppingToken);

        return stdBuffer.ToString();
    }

    public static async Task<string> RunOnce(
        string command,
        IDictionary<string, string?>? environmentVariables = default,
        CancellationToken stoppingToken = default)
    {
        var stdBuffer = new StringBuilder();

        await BuildRun(command, environmentVariables, null, PipeTarget.ToStringBuilder(stdBuffer), PipeTarget.ToStringBuilder(stdBuffer))
            .ExecuteAsync(stoppingToken);

        return stdBuffer.ToString();
    }

    public static IAsyncEnumerable<CommandEvent> RunListen(
        string path,
        string[] args,
        IDictionary<string, string?>? environmentVariables = default,
        PipeSource? inPipeTarget = default,
        CancellationToken stoppingToken = default)
    {
        var osCli = BuildRun(path, args, environmentVariables, inPipeTarget);

        return osCli.ListenAsync(stoppingToken);
    }

    public static async Task RunListenAndLog(
        ILogger logger,
        string path,
        string[] args,
        IDictionary<string, string?>? environmentVariables = default,
        PipeSource? inPipeTarget = default,
        CancellationToken stoppingToken = default)
    {
        await foreach (var cmdEvent in RunListen(path, args, environmentVariables, inPipeTarget, stoppingToken))
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    logger.LogDebug("{x}", stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    logger.LogDebug("{x}", stdErr.Text);
                    break;
                case ExitedCommandEvent exited:
                    var msg = $"{path} ended with return code {exited.ExitCode}";
                    if (exited.ExitCode != 0)
                    {
                        throw new Exception(msg);
                    }
                    else
                    {
                        logger.LogDebug("{x}", msg);
                    }
                    break;
            }
        }
    }

    public static IAsyncEnumerable<CommandEvent> RunListen(
        string command,
        IDictionary<string, string?>? environmentVariables = default,
        PipeSource? inPipeTarget = default,
        CancellationToken stoppingToken = default)
    {
        var osCli = BuildRun(command, environmentVariables, inPipeTarget);

        return osCli.ListenAsync(stoppingToken);
    }

    public static async Task RunListenAndLog(
        ILogger logger,
        string command,
        IDictionary<string, string?>? environmentVariables = default,
        PipeSource? inPipeTarget = default,
        CancellationToken stoppingToken = default)
    {
        await foreach (var cmdEvent in RunListen(command, environmentVariables, inPipeTarget, stoppingToken))
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    logger.LogDebug("{x}", stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    logger.LogDebug("{x}", stdErr.Text);
                    break;
                case ExitedCommandEvent exited:
                    var msg = $"{command} ended with return code {exited.ExitCode}";
                    if (exited.ExitCode != 0)
                    {
                        throw new Exception(msg);
                    }
                    else
                    {
                        logger.LogDebug("{x}", msg);
                    }
                    break;
            }
        }
    }
}
