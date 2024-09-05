using Application.LocalStore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using TransactionHelpers;

namespace Infrastructure.SQLite.LocalStore.Services;

public class SQLiteLocalStoreService(IServiceProvider serviceProvider) : ILocalStoreService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public async Task<Result<string>> Get(string group, string id, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var sqliteLocalDb = scope.ServiceProvider.GetRequiredService<SQLiteLocalStoreGlobalService>();

        Result<string> result = new();

        try
        {
            var value = await Task.Run(async () => await sqliteLocalDb.Get(id, group), cancellationToken);
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
        using var scope = _serviceProvider.CreateScope();
        var sqliteLocalDb = scope.ServiceProvider.GetRequiredService<SQLiteLocalStoreGlobalService>();

        Result<string[]> result = new();

        try
        {
            var value = await Task.Run(async () => await sqliteLocalDb.GetIds(group), cancellationToken);
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
        using var scope = _serviceProvider.CreateScope();
        var sqliteLocalDb = scope.ServiceProvider.GetRequiredService<SQLiteLocalStoreGlobalService>();

        Result result = new();

        try
        {
            await Task.Run(async () => await sqliteLocalDb.Set(id, group, data), cancellationToken);
        }
        catch (Exception ex)
        {
            result.WithError(ex);
        }

        return result;
    }
}
