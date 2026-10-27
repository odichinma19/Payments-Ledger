using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PaymentsLedger.Api.Services;

/// <summary>A business-rule failure that maps directly to an HTTP status code.</summary>
public sealed class LedgerException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Turns <see cref="LedgerException"/> into an RFC 7807 problem response.</summary>
public sealed class LedgerExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not LedgerException ex)
            return false;

        httpContext.Response.StatusCode = ex.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = ex.StatusCode,
                Title = ex.Message
            }
        });
    }
}
