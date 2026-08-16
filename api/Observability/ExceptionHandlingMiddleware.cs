using PatientDataPortal.Api.Errors;

namespace PatientDataPortal.Api.Observability;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException exception)
        {
            logger.LogWarning("Domain request rejected {ErrorCode} {StatusCode}", exception.Code, exception.StatusCode);
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = exception.Code, requestId = context.TraceIdentifier });
        }
        catch (Exception)
        {
            logger.LogError("Unhandled request failure");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "internal_error", requestId = context.TraceIdentifier });
        }
    }
}
