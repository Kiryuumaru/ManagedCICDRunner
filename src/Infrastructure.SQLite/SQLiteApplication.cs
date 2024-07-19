using Application.LocalStore.Interfaces;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.SQLite;

public class SQLiteApplication : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        services.AddSingleton<SQLiteLocalStore>();
        services.AddScoped<ILocalStore, SQLiteLocalStoreProxy>();
    }
}
