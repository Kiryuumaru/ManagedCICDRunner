using AbsolutePathHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

internal static class AbsolutePathExtensions
{
    public static Task WaitKillExceptHyperv(this AbsolutePath path, CancellationToken cancellationToken)
    {
        return WaitKillAll(path, ["system", "vmms", "vmwp"], cancellationToken);
    }

    public static Task<int> KillExceptHyperv(this AbsolutePath path, CancellationToken cancellationToken)
    {
        return KillAll(path, ["system", "vmms", "vmwp"], cancellationToken);
    }

    public static async Task WaitKillAll(this AbsolutePath path, string[] procExceptions, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await KillAll(path, procExceptions, cancellationToken) <= 0)
            {
                break;
            }
            try
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch { }
        }
    }

    public static async Task<int> KillAll(this AbsolutePath path, string[] procExceptions, CancellationToken cancellationToken)
    {
        var processes = await path.GetProcesses(cancellationToken: cancellationToken);
        if (processes.Length == 0)
        {
            return 0;
        }
        foreach (var process in processes)
        {
            try
            {
                var procName = process.ProcessName;
                if (!procExceptions.Any(i => i.Equals(procName, StringComparison.InvariantCultureIgnoreCase)))
                {
                    process.Kill();
                }
            }
            catch { }
        }
        return processes.Length;
    }
}
