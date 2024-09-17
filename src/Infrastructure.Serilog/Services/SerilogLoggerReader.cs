using AbsolutePathHelpers;
using Application;
using Application.Common;
using Application.Configuration.Extensions;
using Application.Logger.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact.Reader;
using Serilog.Parsing;
using System.Globalization;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Infrastructure.Serilog.Services;

internal class SerilogLoggerReader(IConfiguration configuration) : ILoggerReader
{
    private readonly IConfiguration _configuration = configuration;

    public async Task Start(int tail, bool follow, Dictionary<string, string> scope, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? logFileCts = null;
        Guid lastLog = Guid.Empty;
        Guid headruntimeGuid = Guid.Empty;
        bool isWithinScope(LogEvent logEvent)
        {
            return scope.Count == 0 ||
                scope.All(i =>
                    logEvent.Properties.TryGetValue(i.Key, out var scopeProp) &&
                    scopeProp is ScalarValue scopeScalar &&
                    scopeScalar.Value?.ToString() == i.Value);
        }
        void stampLog(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue("EventGuid", out var eventGuidProp) &&
                eventGuidProp is ScalarValue eventGuidScalar &&
                Guid.TryParse(eventGuidScalar.Value?.ToString()!, out var eventGuid))
            {
                lastLog = eventGuid;
            }
            if (logEvent.Properties.TryGetValue("IsHeadLog", out var isHeadLogProp) &&
                isHeadLogProp is ScalarValue isHeadLogScalar &&
                bool.TryParse(isHeadLogScalar.Value?.ToString()!, out bool isHeadLog) &&
                isHeadLog &&
                logEvent.Properties.TryGetValue("RuntimeGuid", out var runtimeGuidProp) &&
                runtimeGuidProp is ScalarValue runtimeGuidScalar &&
                Guid.TryParse(runtimeGuidScalar.Value?.ToString()!, out var runtimeGuid) &&
                headruntimeGuid != runtimeGuid)
            {
                headruntimeGuid = runtimeGuid;
                Log.Write(FromLogEvent(logEvent, "===================================================="));
                Log.Write(FromLogEvent(logEvent, " Service started: {timestamp}", ("timestamp", logEvent.Timestamp)));
                Log.Write(FromLogEvent(logEvent, " Runtime ID: {runtimeGuid}", ("runtimeGuid", runtimeGuid)));
                Log.Write(FromLogEvent(logEvent, "===================================================="));
            }
        }
        void printLogEvent(LogEvent logEvent)
        {
            Log.Write(logEvent);
        }
        async Task printLogEventTail(int count, CancellationToken cancellationToken)
        {
            List<AbsolutePath> scannedLogFiles = [];
            List<(bool WithinScope, LogEvent LogEvent)> logEvents = [];
            int printedLines = 0;
            while (true)
            {
                if (count <= printedLines)
                {
                    break;
                }

                var latestLogFile = await GetLatestLogFile([.. scannedLogFiles], cancellationToken);

                if (latestLogFile == null)
                {
                    break;
                }

                scannedLogFiles.Add(latestLogFile);

                using var fileStream = new FileStream(latestLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var streamReader = new StreamReader(fileStream);

                foreach (var line in (await streamReader.ReadToEndAsync(cancellationToken)).Split(Environment.NewLine).Reverse())
                {
                    if (count <= printedLines)
                    {
                        break;
                    }
                    LogEvent? logEvent = null;
                    try
                    {
                        logEvent = LogEventReader.ReadFromString(line);
                    }
                    catch { }
                    if (logEvent != null)
                    {
                        var inScope = isWithinScope(logEvent);
                        logEvents.Add((inScope, logEvent));
                        if (inScope)
                        {
                            printedLines++;
                        }
                    }
                }
            }
            foreach (var (withinScope, logEvent) in logEvents.ToArray().Reverse())
            {
                stampLog(logEvent);
                if (withinScope)
                {
                    printLogEvent(logEvent);
                }
            }
        }

        await printLogEventTail(tail, cancellationToken);

        if (!follow)
        {
            return;
        }

        bool hasPrintedTail = false;
        await LatestFileListener(async logFile =>
        {
            logFileCts?.Cancel();
            logFileCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = logFileCts.Token;
            try
            {
                using var fileStream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var streamReader = new StreamReader(fileStream);

                if (!hasPrintedTail)
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            try
                            {
                                var line = await streamReader.ReadLineAsync(ct.WithTimeout(TimeSpan.FromMilliseconds(500)));
                                var logEventTail = LogEventReader.ReadFromString(line!);
                                if (logEventTail.Properties.TryGetValue("EventGuid", out var logEventTailProp) &&
                                    logEventTailProp is ScalarValue logEventTailScalar &&
                                    Guid.TryParse(logEventTailScalar.Value?.ToString(), out var logEventTailGuid) &&
                                    logEventTailGuid == lastLog)
                                {
                                    break;
                                }
                            }
                            catch
                            {
                                break;
                            }
                        }
                    }
                    catch { }
                    hasPrintedTail = true;
                }

                while (!ct.IsCancellationRequested)
                {
                    LogEvent? logEvent = null;
                    try
                    {
                        string? line = await streamReader.ReadLineAsync(ct);
                        if (line != null)
                        {
                            logEvent = LogEventReader.ReadFromString(line);
                        }
                    }
                    catch { }
                    if (logEvent != null)
                    {
                        stampLog(logEvent);
                        if (isWithinScope(logEvent))
                        {
                            printLogEvent(logEvent);
                        }
                    }
                    else
                    {
                        await Task.Delay(10);
                    }
                }
            }
            catch { }
        }, cancellationToken);
    }

    private LogEvent FromLogEvent(LogEvent baseLogEvent, string text, params (string Key, object Value)[] properties)
    {
        var props = baseLogEvent.Properties
            .Where(i => i.Key != "EventGuid")
            .Select(i => new LogEventProperty(i.Key, i.Value))
            .ToList();
        props.Add(new LogEventProperty("EventGuid", new ScalarValue(Guid.NewGuid())));
        foreach (var (Key, Value) in properties)
        {
            props.Add(new LogEventProperty(Key, new ScalarValue(Value)));
        }
        return new LogEvent(baseLogEvent.Timestamp, LogEventLevel.Information, null, new MessageTemplateParser().Parse(text), props);
    }

    private async Task LatestFileListener(Action<AbsolutePath> onLogfileChanged, CancellationToken cancellationToken)
    {
        AbsolutePath? logFile = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var latestLogFile = await GetLatestLogFile([], cancellationToken);
            if (latestLogFile != null && logFile != latestLogFile)
            {
                onLogfileChanged(latestLogFile);
                logFile = latestLogFile;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private Task<AbsolutePath?> GetLatestLogFile(AbsolutePath[] skipLogFiles, CancellationToken cancellationToken)
    {
        static (string? LogStr, DateTime LogDateTime) GetLogTime(AbsolutePath logPath)
        {
            var logFileName = logPath.Name;
            if (!logFileName.StartsWith("log-") || !logFileName.EndsWith(".jsonl"))
            {
                return (default, default);
            }
            var currentLogStr = logFileName.Replace("log-", "").Replace(".jsonl", "");
            DateTime currentLog = currentLogStr.Length switch
            {
                12 => DateTime.ParseExact(currentLogStr, "yyyyMMddHHmm", CultureInfo.InvariantCulture),
                10 => DateTime.ParseExact(currentLogStr, "yyyyMMddHH", CultureInfo.InvariantCulture),
                8 => DateTime.ParseExact(currentLogStr, "yyyyMMdd", CultureInfo.InvariantCulture),
                6 => DateTime.ParseExact(currentLogStr, "yyyyMM", CultureInfo.InvariantCulture),
                _ => throw new Exception()
            };
            return (currentLogStr, currentLog);
        }
        return Task.Run(() =>
        {
            (string? LogStr, DateTime LogDateTime) latestLogTime = (default, default);
            foreach (var logFile in (_configuration.GetDataPath() / "logs").GetFiles())
            {
                try
                {
                    var currentLogTime = GetLogTime(logFile);
                    bool skip = false;
                    foreach (var skipLogFile in skipLogFiles)
                    {
                        if (GetLogTime(skipLogFile).LogStr == currentLogTime.LogStr)
                        {
                            skip = true;
                            break;
                        }
                    }
                    if (skip)
                    {
                        continue;
                    }
                    if (latestLogTime.LogStr == null || latestLogTime.LogDateTime < currentLogTime.LogDateTime)
                    {
                        latestLogTime = currentLogTime;
                        continue;
                    }
                }
                catch { }
            }
            if (latestLogTime.LogStr == null)
            {
                return null;
            }
            return AbsolutePath.Create(_configuration.GetDataPath() / "logs" / $"log-{latestLogTime.LogStr}.jsonl");
        }, cancellationToken);
    }
}
