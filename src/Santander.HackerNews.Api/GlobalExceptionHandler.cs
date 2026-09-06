using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Santander.HackerNews.Api;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        int status = exception switch
        {
            TimeoutException => StatusCodes.Status504GatewayTimeout,
            HttpRequestException => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };

        context.Response.StatusCode = status;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = status switch
            {
                503 => "Upstream service unavailable",
                504 => "Upstream service timed out",
                _ => "Unexpected server error"
            }
        }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);

        return true;
    }
}
