using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

public static class ILoggerExtensions
{
    public static IDisposable? BeginScopeMap(this ILogger logger, Dictionary<string, object> scopeMap)
    {
        return logger.BeginScope(scopeMap);
    }
}
