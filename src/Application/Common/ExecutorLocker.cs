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

    private readonly Dictionary<string, SemaphoreSlimTracker> _lockers = [];
    private readonly SemaphoreSlim _lockersLocker = new(1);

    public async Task Execute(string[] keys, Func<Task> execute)
    {
        try
        {
            await Wait(keys);
            await execute();
        }
        catch
        {
            throw;
        }
        finally
        {
            await Release(keys);
        }
    }

    public Task Execute(string key, Func<Task> execute)
    {
        return Execute([key], execute);
    }

    private async Task Wait(string[] keys)
    {
        List<SemaphoreSlimTracker> lockers = [];
        List<Task> tasks = [];
        try
        {
            await _lockersLocker.WaitAsync();
            foreach (var key in keys)
            {
                if (!_lockers.TryGetValue(key, out var locker))
                {
                    locker = new(1);
                    _lockers.Add(key, locker);
                }
                locker.Count++;
                lockers.Add(locker);
                tasks.Add(locker.WaitAsync());
            }
            while (true)
            {
                bool isAllWaited = true;
                List<Task> isAllWaitedTasks = [];
                foreach (var locker in lockers)
                {
                    isAllWaitedTasks.Add(Task.Run(async () =>
                    {
                        if (await locker.WaitAsync(10))
                        {
                            isAllWaited = false;
                        }
                    }));
                }
                await Task.WhenAll(isAllWaitedTasks);
                if (isAllWaited)
                {
                    break;
                }
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            _lockersLocker.Release();
        }
        await Task.WhenAll(tasks);
    }

    private async Task Release(string[] keys)
    {
        try
        {
            await _lockersLocker.WaitAsync();
            foreach (var key in keys)
            {
                if (_lockers.TryGetValue(key, out SemaphoreSlimTracker? locker))
                {
                    locker.Count--;
                    if (locker.Count <= 0)
                    {
                        _lockers.Remove(key);
                    }
                    locker.Release();
                }
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            _lockersLocker.Release();
        }
    }
}
