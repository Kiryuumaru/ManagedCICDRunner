using Application.Common;
using Application.LocalStore.Common;
using Application.LocalStore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Exceptions;

namespace Application.LocalStore.Services;

public class LocalStoreService(ILocalStoreService localStore, IServiceProvider serviceProvider) : LocalStoreImpl(localStore, serviceProvider)
{
    public override async Task<Result<bool>> Contains(string id, string? group = null, CancellationToken cancellationToken = default)
    {
        var concurrencyService = ServiceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        using var scope = await concurrencyService.Aquire(cancellationToken);
        return await CoreContains(id, group, cancellationToken);
    }

    public override async Task<Result> ContainsOrError(string id, string? group = null, CancellationToken cancellationToken = default)
    {
        var concurrencyService = ServiceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        using var scope = await concurrencyService.Aquire(cancellationToken);
        return await CoreContainsOrError(id, group, cancellationToken);
    }

    public override async Task<Result<T>> Get<T>(string id, string? group = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var concurrencyService = ServiceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        using var scope = await concurrencyService.Aquire(cancellationToken);
        return await CoreGet<T>(id, group, cancellationToken);
    }

    public override async Task<Result<string[]>> GetIds(string? group = null, CancellationToken cancellationToken = default)
    {
        var concurrencyService = ServiceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        using var scope = await concurrencyService.Aquire(cancellationToken);
        return await CoreGetIds(group, cancellationToken);
    }

    public override async Task<Result> Set<T>(string id, T? obj, string? group = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var concurrencyService = ServiceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        using var scope = await concurrencyService.Aquire(cancellationToken);
        return await CoreSet(id, obj, group, cancellationToken);
    }

    public override async Task<Result<bool>> Delete(string id, string? group = null, CancellationToken cancellationToken = default)
    {
        var concurrencyService = ServiceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        using var scope = await concurrencyService.Aquire(cancellationToken);
        return await CoreDelete(id, group, cancellationToken);
    }

    public async Task<ConcurrentLocalStore> Transaction(string? group = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(group))
        {
            group = CommonGroup;
        }
        var concurrencyService = ServiceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        var concurrentLocalStore = new ConcurrentLocalStore(LocalStore, ServiceProvider, await concurrencyService.Aquire(cancellationToken))
        {
            CommonGroup = group
        };
        return concurrentLocalStore;
    }
}
