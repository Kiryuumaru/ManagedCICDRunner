using AbsolutePathHelpers;
using Application;
using Application.Common;
using Application.Logger.Interfaces;
using ApplicationBuilderHelpers;
using CliWrap.EventStream;
using CommandLine;
using CommandLine.Text;
using Infrastructure.Serilog;
using Infrastructure.SQLite;
using Infrastructure.SQLite.LocalStore;
using Microsoft.Extensions.Configuration;
using Presentation;
using Presentation.Common;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

ApplicationHostBuilder<WebApplicationBuilder> appBuilder = ApplicationHost.FromBuilder(WebApplication.CreateBuilder(args))
    .Add<Presentation.Presentation>()
    .Add<SerilogInfrastructure>()
    .Add<SQLiteLocalStoreInfrastructure>();

var parserResult = new Parser(with =>
    {
        with.CaseInsensitiveEnumValues = true;
        with.CaseSensitive = false;
        with.IgnoreUnknownArguments = false;
    })
    .ParseArguments<RunOption, ServiceOptions, LogsOptions>(args);

return await parserResult
    .WithNotParsed(_ => DisplayHelp(parserResult))
    .MapResult(
        async (RunOption opts) =>
        {
            if (Validate(parserResult, opts))
            {
                if (opts.AsService)
                {
                    appBuilder.Configuration["MAKE_LOGS"] = "svc";
                }

                await appBuilder.Build().Run();
                return 0;
            }
            return -1;
        },
        async (ServiceOptions opts) =>
        {
            if (Validate(parserResult, opts))
            {
                var ct = SetupCli(opts.LogLevel);
                try
                {
                    if (opts.Install)
                    {
                        await ServiceExtension.InstallAsService(ct);
                    }
                    else if (opts.Uninstall)
                    {
                        await ServiceExtension.UninstallAsService(ct);
                    }
                }
                catch (OperationCanceledException) { }
                return 0;
            }
            return -1;
        },
        async (LogsOptions opts) =>
        {
            if (Validate(parserResult, opts))
            {
                var ct = SetupCli(opts.LogLevel);
                var host = appBuilder.Build();
                var loggerReader = host.Host.Services.GetRequiredService<ILoggerReader>();
                try
                {
                    await loggerReader.Start(opts.Tail, opts.Follow, opts.ScopePairs!, ct);
                }
                catch (OperationCanceledException) { }
                return 0;
            }
            return -1;
        },
        errs => Task.FromResult(-1));

CancellationToken SetupCli(LogEventLevel logEventLevel)
{
    appBuilder.Configuration["LOGGER_LEVEL"] = logEventLevel switch
    {
        LogEventLevel.Verbose => LogLevel.Trace.ToString(),
        LogEventLevel.Debug => LogLevel.Debug.ToString(),
        LogEventLevel.Information => LogLevel.Information.ToString(),
        LogEventLevel.Warning => LogLevel.Warning.ToString(),
        LogEventLevel.Error => LogLevel.Error.ToString(),
        LogEventLevel.Fatal => LogLevel.Critical.ToString(),
        _ => throw new NotImplementedException(logEventLevel.ToString())
    };
    CancellationTokenSource cts = new();
    Console.CancelKeyPress += (s, e) =>
    {
        cts.Cancel();
    };
    return cts.Token;
}

void DisplayHelp<T>(ParserResult<T> result)
{
    if (result.Errors.IsVersion())
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        FileVersionInfo fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
        Console.WriteLine(fileVersionInfo.ProductVersion);
    }
    else
    {
        Console.WriteLine(HelpText.AutoBuild(result, help =>
        {
            help.AddEnumValuesToHelpText = true;
            help.AutoHelp = true;
            help.AutoVersion = true;
            help.AddDashesToOption = true;

            help.AddOptions(result);

            return HelpText.DefaultParsingErrorsHandler(result, help);

        }, e => e));
    }
}

bool Validate<T>(ParserResult<T> parserResult, IArgumentValidation argsToValidate)
{
    try
    {
        argsToValidate.Validate();
        return true;
    }
    catch (ArgumentValidationException ex)
    {
        Console.WriteLine();
        Console.WriteLine("Invalid arguments detected: {0}", ex.Message);
        Console.WriteLine();
        DisplayHelp(parserResult);
        return false;
    }
}

[Verb("run", HelpText = "Run application")]
class RunOption : IArgumentValidation
{
    [Option('s', "as-service", Required = false, HelpText = "Run as service mode.")]
    public bool AsService { get; set; }

    public void Validate()
    {
    }
}

[Verb("service", HelpText = "Service manager")]
class ServiceOptions : IArgumentValidation
{
    [Option("install", Required = false, HelpText = "Install service.")]
    public bool Install { get; set; }

    [Option("uninstall", Required = false, HelpText = "Uninstall service.")]
    public bool Uninstall { get; set; }

    [Option('l', "level", Required = false, HelpText = "Level of logs to show.", Default = LogEventLevel.Information)]
    public LogEventLevel LogLevel { get; set; }

    public void Validate()
    {
        if (!Install && !Uninstall)
        {
            throw new ArgumentValidationException($"No operation selected");
        }
    }
}

[Verb("logs", HelpText = "Get logs.")]
class LogsOptions : IArgumentValidation
{
    [Option('t', "tail", Required = false, HelpText = "Log lines print.", Default = 10)]
    public int Tail { get; set; }

    [Option('f', "follow", Required = false, HelpText = "Follows logs.")]
    public bool Follow { get; set; }

    [Option('l', "level", Required = false, HelpText = "Level of logs to show.", Default = LogEventLevel.Information)]
    public LogEventLevel LogLevel { get; set; }

    [Option('s', "scope", Required = false, HelpText = "Scope of logs.")]
    public IEnumerable<string>? Scope { get; set; }

    public Dictionary<string, string>? ScopePairs { get; set; }

    public void Validate()
    {
        Dictionary<string, string> scopePairs = [];
        if (Scope != null)
        {
            foreach (var s in Scope)
            {
                try
                {
                    var pair = s.Split('=');
                    if (pair.Length != 2)
                    {
                        throw new Exception();
                    }
                    scopePairs[pair[0]] = pair[1];
                }
                catch
                {
                    throw new ArgumentValidationException($"Invalid scope value {s}");
                }
            }
        }
        ScopePairs = scopePairs;
    }
}

class ArgumentValidationException(string message) : Exception(message)
{
}

interface IArgumentValidation
{
    void Validate();
}
