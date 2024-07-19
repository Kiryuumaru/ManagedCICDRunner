using Application.Common;
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

public abstract class LocalStoreImpl(ILocalStore localStore, IServiceProvider serviceProvider)
{
    protected readonly ILocalStore LocalStore = localStore;
    protected readonly IServiceProvider ServiceProvider = serviceProvider;

    public string CommonGroup { get; set; } = "common_store";

    protected async Task<Result<bool>> CoreContains(string id, string? group, CancellationToken cancellationToken)
    {
        Result<bool> result = new();

        if (string.IsNullOrEmpty(group))
        {
            group = CommonGroup;
        }

        if (!result.Success(await LocalStore.Get(group, id, cancellationToken), out string? value))
        {
            return result;
        }

        result.WithValue(!string.IsNullOrEmpty(value));

        return result;
    }

    protected async Task<Result> CoreContainsOrError(string id, string? group, CancellationToken cancellationToken)
    {
        Result result = new();

        if (string.IsNullOrEmpty(group))
        {
            group = CommonGroup;
        }

        if (!result.Success(await LocalStore.Get(group, id, cancellationToken), out string? value))
        {
            return result;
        }

        if (string.IsNullOrEmpty(value))
        {
            result.WithError(new Exception(id + " does not exists"));
        }

        return result;
    }

    protected async Task<Result<T>> CoreGet<T>(string id, string? group, CancellationToken cancellationToken)
        where T : class
    {
        Result<T> result = new();

        if (string.IsNullOrEmpty(group))
        {
            group = CommonGroup;
        }

        if (!result.Success(await LocalStore.Get(group, id, cancellationToken), out string? value))
        {
            return result;
        }

        try
        {
            if (!string.IsNullOrEmpty(value))
            {
                T? obj = JsonSerializer.Deserialize<T>(value, JsonSerializerExtension.CamelCaseOption);
                result.WithValue(obj);
            }
            else
            {
                result.WithValue((T?)default);
            }
        }
        catch (Exception ex)
        {
            result.WithError(ex);
        }

        return result;
    }

    protected async Task<Result<string[]>> CoreGetIds(string? group, CancellationToken cancellationToken)
    {
        Result<string[]> result = new();

        if (string.IsNullOrEmpty(group))
        {
            group = CommonGroup;
        }

        if (!result.Success(await LocalStore.GetIds(group, cancellationToken), out string[]? ids))
        {
            return result;
        }

        result.WithValue(ids);

        return result;
    }

    protected async Task<Result> CoreSet<T>(string id, T? obj, string? group, CancellationToken cancellationToken)
        where T : class
    {
        Result result = new();

        if (string.IsNullOrEmpty(group))
        {
            group = CommonGroup;
        }

        string data;
        try
        {
            data = JsonSerializer.Serialize(obj, JsonSerializerExtension.CamelCaseOption);
        }
        catch (Exception ex)
        {
            result.WithError(ex);
            return result;
        }

        if (!result.Success(await LocalStore.Set(group, id, data, cancellationToken)))
        {
            return result;
        }

        return result;
    }

    protected async Task<Result<bool>> CoreDelete(string id, string? group, CancellationToken cancellationToken)
    {
        Result<bool> result = new();

        if (string.IsNullOrEmpty(group))
        {
            group = CommonGroup;
        }

        if (!result.Success(await LocalStore.Get(group, id, cancellationToken), out string? value))
        {
            return result;
        }

        if (!result.Success(await LocalStore.Set(group, id, null, cancellationToken)))
        {
            return result;
        }

        result.WithValue(!string.IsNullOrEmpty(value));

        return result;
    }

    public abstract Task<Result<bool>> Contains(string id, string? group = null, CancellationToken cancellationToken = default);

    public abstract Task<Result> ContainsOrError(string id, string? group = null, CancellationToken cancellationToken = default);

    public abstract Task<Result<T>> Get<T>(string id, string? group = null, CancellationToken cancellationToken = default) where T : class;

    public abstract Task<Result<string[]>> GetIds(string? group = null, CancellationToken cancellationToken = default);

    public abstract Task<Result> Set<T>(string id, T? obj, string? group = null, CancellationToken cancellationToken = default) where T : class;

    public abstract Task<Result<bool>> Delete(string id, string? group = null, CancellationToken cancellationToken = default);
}
