using Application.Common;
using Application.LocalStore.Services;
using Application.Runner.Services;
using Application.Runner.Workers;
using Application.Vagrant.Services;
using ApplicationBuilderHelpers;
using Domain.Runner.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public class Application : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

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
