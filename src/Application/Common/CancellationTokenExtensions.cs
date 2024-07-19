using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common;

public static class CancellationTokenExtensions
{
    public static CancellationToken WithTimeout(this CancellationToken cancellationToken, TimeSpan timeout)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(timeout).Token).Token;
    }

    public static Task WhenCanceled(this CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        cancellationToken.Register(s => ((TaskCompletionSource<bool>)s!).SetResult(true), tcs);
        return tcs.Task;
    }
}
