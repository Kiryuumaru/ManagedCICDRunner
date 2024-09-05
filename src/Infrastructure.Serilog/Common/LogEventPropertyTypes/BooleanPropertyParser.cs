using Infrastructure.Serilog.Abstractions;

namespace Infrastructure.Serilog.Common.LogEventPropertyTypes;

internal class BooleanPropertyParser : LogEventPropertyParser<bool>
{
    public static BooleanPropertyParser Default { get; } = new();

    public override object? Parse(string? dataStr)
    {
        if (bool.TryParse(dataStr, out var result))
        {
            return result;
        }
        return null;
    }
}
