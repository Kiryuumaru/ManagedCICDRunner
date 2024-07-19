using Application.Common;
using Application.Docker.Services;
using Application.LocalStore.Services;
using Application.Runner.Services;
using Application.Runner.Workers;
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

        services.AddScoped<DockerService>();

        services.AddScoped<RunnerService>();
        services.AddScoped<RunnerStoreService>();
        services.AddHostedService<RunnerWorker>();
        services.AddSingletonObjectHolder<RunnerRuntime[]>();
    }
}
