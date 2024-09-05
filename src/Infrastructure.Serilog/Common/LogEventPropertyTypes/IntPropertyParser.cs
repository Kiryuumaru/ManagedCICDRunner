using Infrastructure.Serilog.Abstractions;

namespace Infrastructure.Serilog.Common.LogEventPropertyTypes;

internal class IntPropertyParser : LogEventPropertyParser<int>
{
    public static IntPropertyParser Default { get; } = new();

    public override object? Parse(string? dataStr)
    {
        if (int.TryParse(dataStr, out var result))
        {
            return result;
        }
        return null;
    }
}
