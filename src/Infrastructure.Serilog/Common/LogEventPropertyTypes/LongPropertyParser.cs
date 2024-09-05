using Infrastructure.Serilog.Abstractions;

namespace Infrastructure.Serilog.Common.LogEventPropertyTypes;

internal class LongPropertyParser : LogEventPropertyParser<long>
{
    public static LongPropertyParser Default { get; } = new();

    public override object? Parse(string? dataStr)
    {
        if (long.TryParse(dataStr, out var result))
        {
            return result;
        }
        return null;
    }
}
