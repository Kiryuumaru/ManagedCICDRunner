using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Logger.Interfaces;

public interface ILoggerReader
{
    Task Start(int tail, bool follow, Dictionary<string, string> scope, CancellationToken cancellationToken = default);
}
