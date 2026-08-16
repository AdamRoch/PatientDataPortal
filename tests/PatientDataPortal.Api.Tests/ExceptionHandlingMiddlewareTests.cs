using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PatientDataPortal.Api.Errors;
using PatientDataPortal.Api.Observability;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task DomainException_ReturnsItsSafeClientError()
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new DomainException("slot_unavailable", "Slot 42 for Ada Lovelace is booked.", StatusCodes.Status409Conflict),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "req_test";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Contains("slot_unavailable", body);
        Assert.Contains("req_test", body);
        Assert.DoesNotContain("Ada Lovelace", body);
        Assert.DoesNotContain("Slot 42", body);
    }
}
