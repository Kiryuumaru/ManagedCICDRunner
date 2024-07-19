using Application.Common;
using Microsoft.Extensions.DependencyInjection;
using SQLite;
using SQLitePCL;
using TransactionHelpers;

namespace Infrastructure.SQLite;

internal class SQLiteLocalStore
{
    //private readonly SQLiteAsyncConnection _db = new(AbsolutePath.Parse(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) / "netconduit.dat");
    private readonly SQLiteAsyncConnection _db = new(AbsolutePath.Parse(Environment.CurrentDirectory) / "dat.dat");

    private async Task Bootstrap()
    {
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
