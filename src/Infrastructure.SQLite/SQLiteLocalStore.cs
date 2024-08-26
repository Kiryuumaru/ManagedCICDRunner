using Application;
using Application.Common;
using Microsoft.Extensions.DependencyInjection;
using SQLite;
using SQLitePCL;
using TransactionHelpers;

namespace Infrastructure.SQLite;

internal class SQLiteLocalStore
{
    private static readonly AbsolutePath _dbPath = Defaults.DataPath / ".db";
    private readonly SQLiteAsyncConnection _db = new(_dbPath);

    private async Task Bootstrap()
    {
        await _dbPath.Parent.CreateDirectory();
        await _db.CreateTableAsync<SQLiteDataHolder>();
    }

    public async Task<string?> Get(string id, string group)
    {
        await Bootstrap();

        string rawId = id + "__" + group;

        var getItems = await _db.Table<SQLiteDataHolder>()
            .Where(i => i.Group == group)
            .Where(i => i.Id == rawId)
            .ToListAsync();

        return getItems.FirstOrDefault()?.Data;
    }

    public async Task<string[]> GetIds(string group)
    {
        await Bootstrap();

        var query = "select \"" + nameof(SQLiteDataHolder.Id) + "\" from \"" + nameof(SQLiteDataHolder) + "\" where \"Group\" = \"" + group + "\"";
        var idHolders = await _db.QueryAsync<SQLiteDataIdHolder>(query);

        var idPostfix = "__" + group;

        return idHolders
            .Where(i => i.Id != null)
            .Where(i => i.Id!.Contains(idPostfix))
            .Select(i => i.Id![..i.Id!.LastIndexOf(idPostfix)])
            .ToArray();
    }

    public async Task Set(string id, string group, string? data)
    {
        await Bootstrap();

        string rawId = id + "__" + group;

        if (data == null)
        {
            await _db.Table<SQLiteDataHolder>()
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
            await _db.InsertOrReplaceAsync(stock);
        }
    }
}
