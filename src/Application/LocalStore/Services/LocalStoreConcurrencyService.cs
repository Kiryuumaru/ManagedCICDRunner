using Application.Common;
using Application.LocalStore.Interfaces;
using DisposableHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Exceptions;

namespace Application.LocalStore.Services;

internal class LocalStoreConcurrencyService
{
    private readonly SemaphoreSlim _locker = new(1);

    internal async Task<IDisposable> Aquire(CancellationToken cancellationToken = default)
    {
        await _locker.WaitAsync(cancellationToken);
        return new Disposable(disposing =>
        {
            if (disposing)
            {
                _locker.Release();
            }
        });
    }
}
