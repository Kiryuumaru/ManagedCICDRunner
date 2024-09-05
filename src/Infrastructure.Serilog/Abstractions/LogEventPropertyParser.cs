namespace Infrastructure.Serilog.Abstractions;

internal interface ILogEventPropertyParser
{
    string TypeIdentifier { get; }

    object? Parse(string? dataStr);
}

internal abstract class LogEventPropertyParser<T> : ILogEventPropertyParser
{
    public string TypeIdentifier => typeof(T).Name;

    public abstract object? Parse(string? dataStr);
}
