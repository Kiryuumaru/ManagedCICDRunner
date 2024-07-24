using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

internal class ExecutorLocker
{
    class SemaphoreSlimTracker(int initialCount) : SemaphoreSlim(initialCount)
    {
        public int Count { get; set; }
    }

    private readonly Dictionary<string, SemaphoreSlimTracker> lockers = [];
    private readonly SemaphoreSlim lockersLocker = new(1);

    public async Task Execute(string key, Func<Task> execute)
    {
        var locker = await Get(key);

        try
        {
            await locker.WaitAsync();
            await execute();
        }
        catch
        {
            throw;
        }
        finally
        {
            locker.Release();
        }

        await Delete(key);
    }

    private async Task<SemaphoreSlimTracker> Get(string key)
    {
        SemaphoreSlimTracker? locker;
        try
        {
            await lockersLocker.WaitAsync();
            if (!lockers.TryGetValue(key, out locker))
            {
                locker = new(1);
                lockers.Add(key, locker);
            }
            locker.Count++;
        }
        catch
        {
            throw;
        }
        finally
        {
            lockersLocker.Release();
        }
        return locker;
    }

    private async Task Delete(string key)
    {
        try
        {
            await lockersLocker.WaitAsync();
            if (lockers.TryGetValue(key, out SemaphoreSlimTracker? locker))
            {
                locker.Count--;
                if (locker.Count <= 0)
                {
                    lockers.Remove(key);
                }
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            lockersLocker.Release();
        }
    }
}
