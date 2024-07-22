using Application;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Presentation;

internal class BasePresentation : BaseApplication
{
    public override void AddConfiguration(ApplicationDependencyBuilder builder, IConfiguration configuration)
    {
        base.AddConfiguration(builder, configuration);

        (configuration as ConfigurationManager)!.AddEnvironmentVariables();
    }

    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

        services.AddMvc();
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "CICD Self Runner API",
                Description = "Just another CICD auto self runner solution",
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

    public override void AddMiddlewares(ApplicationDependencyBuilder builder, IHost host)
    {
        base.AddMiddlewares(builder, host);

        (host as IApplicationBuilder)!.UseSwagger();
        (host as IApplicationBuilder)!.UseSwaggerUI();

        (host as IApplicationBuilder)!.UseHttpsRedirection();
    }

    public override void AddMappings(ApplicationDependencyBuilder builder, IHost host)
    {
        base.AddMappings(builder, host);

        (host as WebApplication)!.UseHttpsRedirection();
        (host as WebApplication)!.UseAuthorization();
        (host as WebApplication)!.MapControllers();
    }
}
