using Application.Common;
using Application.LocalStore.Services;
using Application.Runner.Services;
using Application.Runner.Workers;
using Application.Vagrant.Services;
using ApplicationBuilderHelpers;
using Domain.Runner.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public class BaseApplication : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

        services.AddTransient<LocalStoreService>();
        services.AddSingleton<LocalStoreConcurrencyService>();

        services.AddSingleton<VagrantService>();

        services.AddScoped<RunnerService>();
        services.AddScoped<RunnerTokenService>();
        services.AddScoped<RunnerStoreService>();
        services.AddScoped<RunnerTokenStoreService>();
        services.AddHostedService<RunnerWorker>();
        services.AddSingletonObjectHolder<Dictionary<string, RunnerRuntime>>();
    }
}
