using AbsolutePathHelpers;
using Application;
using Application.Common;
using Infrastructure.SQLite.LocalStore.Models;
using Infrastructure.SQLite.Services;
using Microsoft.Extensions.DependencyInjection;
using SQLite;
using SQLitePCL;
using TransactionHelpers;

namespace Infrastructure.SQLite.LocalStore.Services;

internal class SQLiteLocalStoreGlobalService(SQLiteGlobalService sqliteGlobalService)
{
    private readonly SQLiteGlobalService _sqliteGlobalService = sqliteGlobalService;

    private readonly SemaphoreSlim _locker = new(1);

    private SQLiteAsyncConnection? _db = null;

    private async ValueTask<SQLiteAsyncConnection> Bootstrap()
    {
        if (_db == null)
        {
            try
            {
                await _locker.WaitAsync();
                _db ??= await _sqliteGlobalService.BootstrapTable<SQLiteDataHolder>();
            }
            finally
            {
                _locker.Release();
            }
        }
        return _db;
    }

    public async Task<string?> Get(string id, string group)
    {
        var db = await Bootstrap();

        string rawId = id + "__" + group;

        var getItems = await db.Table<SQLiteDataHolder>()
            .Where(i => i.Group == group)
            .Where(i => i.Id == rawId)
            .ToListAsync();

        return getItems.FirstOrDefault()?.Data;
    }

    public async Task<string[]> GetIds(string group)
    {
        var db = await Bootstrap();

        var query = "select \"" + nameof(SQLiteDataHolder.Id) + "\" from \"" + nameof(SQLiteDataHolder) + "\" where \"Group\" = \"" + group + "\"";
        var idHolders = await db.QueryAsync<SQLiteDataIdHolder>(query);

        var idPostfix = "__" + group;

        return idHolders
            .Where(i => i.Id != null)
            .Where(i => i.Id!.Contains(idPostfix))
            .Select(i => i.Id![..i.Id!.LastIndexOf(idPostfix)])
            .ToArray();
    }

    public async Task Set(string id, string group, string? data)
    {
        var db = await Bootstrap();

        string rawId = id + "__" + group;

        if (data == null)
        {
            await db.Table<SQLiteDataHolder>()
                .Where(i => i.Group == group)
                .Where(i => i.Id == rawId)
                .DeleteAsync();
        }
        else
        {
            var stock = new SQLiteDataHolder()
            {
                Id = rawId,
                Group = group,
                Data = data,
            };
            await db.InsertOrReplaceAsync(stock);
        }
    }
}
