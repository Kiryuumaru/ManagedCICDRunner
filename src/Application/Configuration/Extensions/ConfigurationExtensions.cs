using AbsolutePathHelpers;
using Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Configuration.Extensions;

public static class ConfigurationExtensions
{
    public static bool ContainsVarRefValue(this IConfiguration configuration, string varName)
    {
        try
        {
            return !string.IsNullOrEmpty(GetVarRefValue(configuration, varName));
        }
        catch
        {
            return false;
        }
    }

    public static string GetVarRefValue(this IConfiguration configuration, string varName)
    {
        string? varValue = $"@ref:{varName}";
        while (true)
        {
            if (varValue.StartsWith("@ref:"))
            {
                varName = varValue[5..];
                varValue = configuration[varName];
                if (string.IsNullOrEmpty(varValue))
                {
                    throw new Exception($"{varName} is empty.");
                }
                continue;
            }
            break;
        }
        return varValue;
    }

    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static string? GetVarRefValueOrDefault(this IConfiguration configuration, string varName, string? defaultValue = null)
    {
        try
        {
            return GetVarRefValue(configuration, varName);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static Guid? _runtimeGuid = null;
    public static Guid GetRuntimeGuid(this IConfiguration configuration)
    {
        if (_runtimeGuid == null)
        {
            var runtimeGuidStr = configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_RUNTIME_GUID", null);
            if (string.IsNullOrEmpty(runtimeGuidStr))
            {
                _runtimeGuid = Guid.NewGuid();
            }
            else
            {
                _runtimeGuid = Guid.Parse(runtimeGuidStr);
            }
        }
        return _runtimeGuid.Value;
    }

    private static bool? _makeFileLogs = null;
    public static bool GetMakeFileLogs(this IConfiguration configuration)
    {
        if (_makeFileLogs == null)
        {
            var makeLogsStr = configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_MAKE_LOGS", "no");
            if (!makeLogsStr.Equals("svc", StringComparison.InvariantCultureIgnoreCase))
            {
                _makeFileLogs = false;
            }
            else
            {
                _makeFileLogs = true;
            }
        }
        return _makeFileLogs.Value;
    }

    private static LogLevel? _loggerLevel = null;
    public static LogLevel GetLoggerLevel(this IConfiguration configuration)
    {
        if (_loggerLevel == null)
        {
            var loggerLevel = configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_LOGGER_LEVEL", LogLevel.Information.ToString());
            _loggerLevel = Enum.Parse<LogLevel>(loggerLevel);
        }
        return _loggerLevel.Value;
    }

    private static AbsolutePath? _dataPath = null;
    public static AbsolutePath GetDataPath(this IConfiguration configuration)
    {
        if (_dataPath == null)
        {
            var dataPath = configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_DATA_PATH", null);
            if (string.IsNullOrEmpty(dataPath))
            {
                //_dataPath = AbsolutePath.Create("C:\\ManagedCICDRunner") / ".data";
                _dataPath = AbsolutePath.Create(Environment.CurrentDirectory) / ".data";
            }
            else
            {
                _dataPath = AbsolutePath.Create(dataPath);
            }
        }
        return _dataPath;
    }
}
