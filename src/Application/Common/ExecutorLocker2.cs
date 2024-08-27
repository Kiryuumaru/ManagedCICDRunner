using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

internal class ExecutorLocker2<TKey>
    where TKey : notnull
{
    class SemaphoreSlimTracker(int initialCount) : SemaphoreSlim(initialCount)
    {
        public int Count { get; set; }
    }

    private readonly Dictionary<TKey, SemaphoreSlimTracker> _lockers = [];
    private readonly SemaphoreSlim _lockersLocker = new(1);

    public async Task Execute(TKey[] keys, Func<Task> execute)
    {
        try
        {
            await Wait(keys, null);
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

    public async Task ExecuteOrSkipAllKey(TKey[] keys, Func<Task> execute)
    {
        bool hasWaited = false;
        try
        {
            if (await Wait(keys, false))
            {
                hasWaited = true;
                await execute();
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            if (hasWaited)
            {
                await Release(keys);
            }
        }
    }

    public async Task ExecuteOrSkipOneKey(TKey[] keys, Func<Task> execute)
    {
        bool hasWaited = false;
        try
        {
            if (await Wait(keys, true))
            {
                hasWaited = true;
                await execute();
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            if (hasWaited)
            {
                await Release(keys);
            }
        }
    }

    public Task Execute(TKey key, Func<Task> execute)
    {
        return Execute([key], execute);
    }

    public Task ExecuteOrSkip(TKey key, Func<Task> execute)
    {
        return ExecuteOrSkipAllKey([key], execute);
    }

    private async Task<bool> Wait(TKey[] keys, bool? oneKey)
    {
        if (keys.Length != keys.Distinct().Count())
        {
            throw new ArgumentException("Args keys has duplicate");
        }

        Dictionary<TKey, SemaphoreSlimTracker> lockers = [];
        List<Task> tasks = [];
        try
        {
            await _lockersLocker.WaitAsync();
            bool shouldWait = true;
            if (oneKey.HasValue)
            {
                shouldWait = !oneKey.Value;
            }
            foreach (var key in keys)
            {
                if (oneKey.HasValue)
                {
                    if (oneKey.Value)
                    {
                        if (!_lockers.TryGetValue(key, out var locker))
                        {
                            locker = new(1);
                            shouldWait = true;
                        }
                        lockers.Add(key, locker);
                    }
                    else
                    {
                        if (_lockers.TryGetValue(key, out var locker))
                        {
                            shouldWait = false;
                            break;
                        }
                    }
                }
                else
                {
                    if (!_lockers.TryGetValue(key, out var locker))
                    {
                        locker = new(1);
                    }
                    lockers.Add(key, locker);
                }
            }
            if (!shouldWait)
            {
                return false;
            }
            foreach (var (key, locker) in lockers)
            {
                _lockers.Add(key, locker);
                locker.Count++;
                tasks.Add(locker.WaitAsync());
            }
            while (true)
            {
                bool isAllWaited = true;
                List<Task> isAllWaitedTasks = [];
                foreach (var (key, locker) in lockers)
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
        return true;
    }

    private async Task Release(TKey[] keys)
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
