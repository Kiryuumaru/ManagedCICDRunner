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

    public static bool GetMakeFileLogs(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_MAKE_LOGS", "no").Equals("yes", StringComparison.InvariantCultureIgnoreCase);
    }
    public static void SetMakeFileLogs(this IConfiguration configuration, bool makeFileLogs)
    {
        configuration["MANAGED_CICD_RUNNER_MAKE_LOGS"] = makeFileLogs ? "yes" : "no";
    }

    public static LogLevel GetLoggerLevel(this IConfiguration configuration)
    {
        var loggerLevel = configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_LOGGER_LEVEL", LogLevel.Information.ToString());
        return Enum.Parse<LogLevel>(loggerLevel);
    }
    public static void SetLoggerLevel(this IConfiguration configuration, LogLevel loggerLevel)
    {
        configuration["MANAGED_CICD_RUNNER_LOGGER_LEVEL"] = loggerLevel.ToString();
    }

    public static AbsolutePath GetDataPath(this IConfiguration configuration)
    {
        //return configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_DATA_PATH", AbsolutePath.Create(Environment.CurrentDirectory) / ".data");
        return configuration.GetVarRefValueOrDefault("MANAGED_CICD_RUNNER_DATA_PATH", AbsolutePath.Create("C:\\ManagedCICDRunner") / ".data");
    }
    public static void SetDataPath(this IConfiguration configuration, AbsolutePath dataPath)
    {
        configuration["MANAGED_CICD_RUNNER_DATA_PATH"] = dataPath;
    }
}
