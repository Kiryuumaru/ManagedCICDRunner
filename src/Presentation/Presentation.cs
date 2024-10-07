using Application;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json;
using Serilog;
using Microsoft.Extensions.Options;

namespace Presentation;

internal class Presentation : Application.Application
{
    public override void AddConfiguration(ApplicationHostBuilder applicationBuilder, IConfiguration configuration)
    {
        base.AddConfiguration(applicationBuilder, configuration);

        (configuration as ConfigurationManager)!.AddEnvironmentVariables();
    }

    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddHttpClient(Options.DefaultName, client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", Defaults.AppNamePascalCase);
        });

        services.AddMvc();
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "CICD Self Runner API",
                Description = "CICD Self Runner API",
                Contact = new OpenApiContact
                {
                    Name = "Kiryuumaru",
                    Url = new Uri("https://github.com/Kiryuumaru")
                }
            });

            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
        });
    }

    public override void AddMiddlewares(ApplicationHost applicationHost, IHost host)
    {
        base.AddMiddlewares(applicationHost, host);

        (host as IApplicationBuilder)!.UseSwagger();
        (host as IApplicationBuilder)!.UseSwaggerUI();

        (host as IApplicationBuilder)!.UseHttpsRedirection();
    }

    public override void AddMappings(ApplicationHost applicationHost, IHost host)
    {
        base.AddMappings(applicationHost, host);

        (host as WebApplication)!.UseHttpsRedirection();
        (host as WebApplication)!.UseAuthorization();
        (host as WebApplication)!.MapControllers();
    }

    public override void RunPreparation(ApplicationHost applicationHost)
    {
        base.RunPreparation(applicationHost);

        Log.Information("Application starting");
    }
}
