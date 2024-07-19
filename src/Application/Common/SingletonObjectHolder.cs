using Domain.Runner.Models;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

internal static class SingletonObjectHolderExtension
{
    public static IServiceCollection AddSingletonObjectHolder<TObj>(this IServiceCollection services)
    {
        services.AddSingleton<SingletonObjectHolder<TObj>>();
        return services;
    }

    public static SingletonObjectHolder<TObj> GetSingletonObjectHolder<TObj>(this IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<SingletonObjectHolder<TObj>>();
    }
}

internal class SingletonObjectHolder<TObj>
{
    private readonly SemaphoreSlim _locker = new(1);
    private Func<Task<TObj?>>? _currentObjFactory;

    public async Task<TObj?> Get()
    {
        try
        {
            await _locker.WaitAsync();

            if (_currentObjFactory != null)
            {
                return await _currentObjFactory();
            }
            else
            {
                return default;
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            _locker.Release();
        }
    }

    public async Task Set(Func<Task<TObj?>> objFactory)
    {
        try
        {
            await _locker.WaitAsync();

            _currentObjFactory = objFactory;
        }
        catch
        {
            throw;
        }
        finally
        {
            _locker.Release();
        }
    }

    public async Task Set(Func<TObj?> objFactory)
    {
        try
        {
            await _locker.WaitAsync();

            _currentObjFactory = () => Task.FromResult(objFactory());
        }
        catch
        {
            throw;
        }
        finally
        {
            _locker.Release();
        }
    }
}
