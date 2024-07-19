using ApplicationBuilderHelpers;
using Infrastructure.SQLite;
using Presentation;

ApplicationDependencyBuilder.FromBuilder(WebApplication.CreateBuilder(args))
    .Add<BasePresentation>()
    .Add<SQLiteApplication>()
    .Run();
