using System.Diagnostics;

namespace PatientDataPortal.Api.Observability;

public sealed class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
{
    private const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = GetRequestId(context.Request.Headers[HeaderName]);
        context.TraceIdentifier = requestId;
        context.Response.Headers[HeaderName] = requestId;

        using (logger.BeginScope(new Dictionary<string, object> { ["requestId"] = requestId }))
        {
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Request started {Method}", context.Request.Method);
            await next(context);
            logger.LogInformation(
                "Request completed {Method} {StatusCode} {ElapsedMs}",
                context.Request.Method,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static string GetRequestId(string? suppliedRequestId) =>
        suppliedRequestId is { Length: > 0 and <= 128 } &&
        suppliedRequestId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? suppliedRequestId
            : Guid.NewGuid().ToString("N");
}
