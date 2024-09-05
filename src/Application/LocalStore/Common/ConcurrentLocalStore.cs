using Application.Common;
using Application.LocalStore.Interfaces;
using Application.LocalStore.Services;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Exceptions;

namespace Application.LocalStore.Common;

[Disposable]
public partial class ConcurrentLocalStore : LocalStoreImpl
{
    private readonly IDisposable _concurrency;

    internal ConcurrentLocalStore(ILocalStoreService localStore, IServiceProvider serviceProvider, IDisposable concurrency)
        : base(localStore, serviceProvider)
    {
        _concurrency = concurrency;
    }

    public override Task<Result<bool>> Contains(string id, string? group = null, CancellationToken cancellationToken = default)
    {
        return CoreContains(id, group, cancellationToken);
    }

    public override Task<Result> ContainsOrError(string id, string? group = null, CancellationToken cancellationToken = default)
    {
        return CoreContainsOrError(id, group, cancellationToken);
    }

    public override Task<Result<T>> Get<T>(string id, string? group = null, CancellationToken cancellationToken = default)
        where T : class
    {
        return CoreGet<T>(id, group, cancellationToken);
    }

    public override Task<Result<string[]>> GetIds(string? group = null, CancellationToken cancellationToken = default)
    {
        return CoreGetIds(group, cancellationToken);
    }

    public override Task<Result> Set<T>(string id, T? obj, string? group = null, CancellationToken cancellationToken = default)
        where T : class
    {
        return CoreSet(id, obj, group, cancellationToken);
    }

    public override Task<Result<bool>> Delete(string id, string? group = null, CancellationToken cancellationToken = default)
    {
        return CoreDelete(id, group, cancellationToken);
    }

    public void Dispose(bool disposing)
    {
        if (disposing)
        {
            _concurrency.Dispose();
        }
    }
}
