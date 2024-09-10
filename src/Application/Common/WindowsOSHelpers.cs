using AbsolutePathHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common;

public static class WindowsOSHelpers
{
    public static async Task TakeOwnPermission(AbsolutePath filePath, CancellationToken cancellationToken)
    {
        await Cli.RunOnce("powershell", [$"TakeOwn /F \"{filePath}\""], stoppingToken: cancellationToken);
        await Cli.RunOnce("powershell", [$"Icacls \"{filePath}\" /C /T /Inheritance:d"], stoppingToken: cancellationToken);
        await Cli.RunOnce("powershell", [$"Icacls \"{filePath}\" /C /T /Remove:g Administrator \\\"Authenticated Users\\\" BUILTIN\\Administrators BUILTIN Everyone System Users"], stoppingToken: cancellationToken);
        await Cli.RunOnce("powershell", [$"Icacls \"{filePath}\" /C /T /Grant ${{env:UserName}}:F"], stoppingToken: cancellationToken);
        await Cli.RunOnce("powershell", [$"Icacls \"{filePath}\" /C /T /Grant:r ${{env:UserName}}:F"], stoppingToken: cancellationToken);
    }

    public static async Task<string[]> GetRequiredFeatures(CancellationToken stoppingToken)
    {
        if (await IsWindowsServer(stoppingToken))
        {
            return ["Hyper-V"];
        }
        else
        {
            return ["Microsoft-Hyper-V-All", "HypervisorPlatform", "VirtualMachinePlatform"];
        }
    }

    public static async Task InstallFeature(string featureName, CancellationToken stoppingToken)
    {
        if (await IsWindowsServer(stoppingToken))
        {
            await Cli.RunOnce("powershell", ["-c", $"Install-WindowsFeature -Name {featureName} -IncludeManagementTools"], stoppingToken: stoppingToken);
        }
        else
        {
            await Cli.RunOnce("powershell", ["-c", $"Enable-WindowsOptionalFeature -FeatureName {featureName} -Online -All"], stoppingToken: stoppingToken);
        }
    }

    public static async Task<bool> IsFeatureEnabled(string featureName, CancellationToken stoppingToken)
    {
        if (await IsWindowsServer(stoppingToken))
        {
            try
            {
                string enabledFeatureRaw = await Cli.RunOnce("powershell", ["-c", $"(Get-WindowsFeature -Name {featureName}).Installed"], stoppingToken: stoppingToken);
                enabledFeatureRaw = enabledFeatureRaw.Trim();
                if (enabledFeatureRaw.Equals("true", StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            catch { }
        }
        else
        {
            try
            {
                string enabledFeatureRaw = await Cli.RunOnce("powershell", ["-c", $"(Get-WindowsOptionalFeature -FeatureName {featureName} -Online).State"], stoppingToken: stoppingToken);
                enabledFeatureRaw = enabledFeatureRaw.Trim();
                if (enabledFeatureRaw.Equals("enabled", StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    public static async Task<bool> IsWindowsServer(CancellationToken stoppingToken)
    {
        string getWinOsType = await Cli.RunOnce("powershell", ["-c", $"(Get-CimInstance Win32_OperatingSystem).Caption"], stoppingToken: stoppingToken);
        return getWinOsType.Trim().StartsWith("Microsoft Windows Server", StringComparison.InvariantCultureIgnoreCase);
    }
}
