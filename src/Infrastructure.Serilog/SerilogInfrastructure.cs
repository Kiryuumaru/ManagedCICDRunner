using Application.Logger.Interfaces;
using ApplicationBuilderHelpers;
using Infrastructure.Serilog.Common;
using Infrastructure.Serilog.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Infrastructure.Serilog;

public class SerilogInfrastructure : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddTransient<ILoggerReader, SerilogLoggerReader>();

        (applicationBuilder.Builder as WebApplicationBuilder)!.Host
            .UseSerilog((context, loggerConfiguration) => LoggerBuilder.Configure(loggerConfiguration, applicationBuilder.Configuration));

        Log.Logger = LoggerBuilder.Configure(new LoggerConfiguration(), applicationBuilder.Configuration).CreateLogger();

        var ss = Log.Logger;
    }

    public override void AddMiddlewares(ApplicationHost applicationHost, IHost host)
    {
        base.AddMiddlewares(applicationHost, host);

        (host as IApplicationBuilder)!.UseSerilogRequestLogging();
    }
}
