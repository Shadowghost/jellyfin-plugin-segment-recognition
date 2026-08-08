using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.SegmentRecognition.Api;

/// <summary>
/// Turns a client-aborted request into a quiet 499 instead of letting the cancellation reach
/// Jellyfin's <c>ExceptionMiddleware</c>, which logs every <see cref="OperationCanceledException"/>
/// at error level ("Error processing request: A task was canceled"). The actions here take a
/// <see cref="System.Threading.CancellationToken"/> bound to <c>HttpContext.RequestAborted</c> and
/// hand it to the database, so a browser navigating away mid-request is an ordinary outcome, not a
/// server fault.
/// </summary>
/// <remarks>
/// Only cancellation that the request itself caused is swallowed. A cancellation raised for any
/// other reason still propagates, so a genuine internal timeout is not disguised as a client abort.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class ClientAbortFilterAttribute : ExceptionFilterAttribute
{
    /// <summary>
    /// The nginx-originated status code for "client closed request". Nothing reads it - the
    /// connection is already gone - but it keeps the response consistent with the log.
    /// </summary>
    private const int StatusClientClosedRequest = 499;

    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Exception is not OperationCanceledException
            || !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        context.ExceptionHandled = true;
        context.Result = new StatusCodeResult(StatusClientClosedRequest);
    }
}
