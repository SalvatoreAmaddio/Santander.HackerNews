using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Santander.HackerNews.Api;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        int statusCode = exception switch
        {
            HttpRequestException => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
                                                    {
                                                        Status = statusCode,
                                                        Title = statusCode == 503
                                                            ? "Upstream service unavailable"
                                                            : "Unexpected server error"
                                                    }, cancellationToken);

        return true;
    }
}