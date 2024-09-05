using AbsolutePathHelpers;
using Application;
using Application.Common;
using Application.Configuration.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SQLite;
using SQLitePCL;
using TransactionHelpers;

namespace Infrastructure.SQLite.Services;

public class SQLiteGlobalService(IConfiguration configuration)
{
    private readonly IConfiguration _configuration = configuration;

    private readonly SemaphoreSlim _locker = new(1);

    private SQLiteAsyncConnection? _db = null;

    public async Task<SQLiteAsyncConnection> Bootstrap()
    {
        if (_db == null)
        {
            try
            {
                await _locker.WaitAsync();
                if (_db == null)
                {
                    var dbPath = _configuration.GetDataPath() / "dat.db";
                    dbPath.Parent?.CreateDirectory();
                    _db = new(dbPath);
                }
            }
            finally
            {
                _locker.Release();
            }
        }
        return _db;
    }

    public async Task<SQLiteAsyncConnection> BootstrapTable<T>()
         where T : new()
    {
        var db = await Bootstrap();
        await db.CreateTableAsync<T>();
        return db;
    }
}
