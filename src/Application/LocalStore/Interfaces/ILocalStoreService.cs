using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.LocalStore.Interfaces;

public interface ILocalStoreService
{
    Task<Result<string>> Get(string group, string id, CancellationToken cancellationToken);

    Task<Result<string[]>> GetIds(string group, CancellationToken cancellationToken);

    Task<Result> Set(string group, string id, string? data, CancellationToken cancellationToken);
}
