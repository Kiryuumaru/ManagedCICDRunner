using Application.LocalStore.Interfaces;
using ApplicationBuilderHelpers;
using Infrastructure.SQLite.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.SQLite;

public class SQLiteInfrastructure : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationHostBuilder, IServiceCollection services)
    {
        base.AddServices(applicationHostBuilder, services);

        services.AddSingleton<SQLiteGlobalService>();
    }
}
