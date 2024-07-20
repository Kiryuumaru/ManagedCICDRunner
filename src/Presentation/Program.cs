using ApplicationBuilderHelpers;
using Infrastructure.SQLite;
using Presentation;

if (args.Any(i => i.Equals("--install-service", StringComparison.InvariantCultureIgnoreCase)))
{
    Console.WriteLine("CSCSC");
}
else
{
    ApplicationDependencyBuilder.FromBuilder(WebApplication.CreateBuilder(args))
        .Add<BasePresentation>()
        .Add<SQLiteApplication>()
        .Run();
}
