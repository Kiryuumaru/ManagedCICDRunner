using Application.LocalStore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using TransactionHelpers;

namespace Infrastructure.SQLite;

public class SQLiteLocalStoreProxy(IServiceProvider serviceProvider) : ILocalStore
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public async Task<Result<string>> Get(string group, string id, CancellationToken cancellationToken)
    {
        var signalRStreamProxy = _serviceProvider.GetRequiredService<SQLiteLocalStore>();

        Result<string> result = new();

        try
        {
            var value = await Task.Run(async () => await signalRStreamProxy.Get(id, group), cancellationToken);
            result.WithValue(value);
        }
        catch (Exception ex)
        {
            result.WithError(ex);
        }

        return result;
    }

    public async Task<Result<string[]>> GetIds(string group, CancellationToken cancellationToken)
    {
        var signalRStreamProxy = _serviceProvider.GetRequiredService<SQLiteLocalStore>();

        Result<string[]> result = new();

        try
        {
            var value = await Task.Run(async () => await signalRStreamProxy.GetIds(group), cancellationToken);
            result.WithValue(value);
        }
        catch (Exception ex)
        {
            result.WithError(ex);
        }

        return result;
    }

    public async Task<Result> Set(string group, string id, string? data, CancellationToken cancellationToken)
    {
        var signalRStreamProxy = _serviceProvider.GetRequiredService<SQLiteLocalStore>();

        Result result = new();

        try
        {
            await Task.Run(async () => await signalRStreamProxy.Set(id, group, data), cancellationToken);
        }
        catch (Exception ex)
        {
            result.WithError(ex);
        }

        return result;
    }
}
