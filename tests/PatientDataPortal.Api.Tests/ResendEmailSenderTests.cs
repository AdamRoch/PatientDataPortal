using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Email;
using Xunit;
using Xunit.Sdk;

namespace PatientDataPortal.Api.Tests;

public sealed class ResendEmailSenderTests
{
    [Fact]
    public async Task LogMode_ReturnsDeterministicDevelopmentMessageId()
    {
        var sender = CreateSender(new EmailOptions { DeliveryMode = "log" });

        var result = await sender.SendAsync(Message("outbox/share/123"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("dev_outbox/share/123", result.ProviderMessageId);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task ResendMode_PassesStableIdempotencyKeyAndReturnsProviderMessageId()
    {
        HttpRequestMessage? capturedRequest = null;
        var sender = CreateSender(
            new EmailOptions { DeliveryMode = "resend", ApiKey = "test-key", From = "portal@example.test" },
            request =>
            {
                capturedRequest = request;
                return JsonResponse(HttpStatusCode.OK, "{\"id\":\"email_123\"}");
            });

        var result = await sender.SendAsync(Message("outbox/share/123"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("email_123", result.ProviderMessageId);
        Assert.Equal("outbox/share/123", capturedRequest!.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("Bearer test-key", capturedRequest.Headers.Authorization.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, EmailFailureKind.RateLimited, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, EmailFailureKind.ProviderUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, EmailFailureKind.Rejected, false)]
    public async Task ProviderFailures_ReturnStructuredTaxonomy(HttpStatusCode statusCode, EmailFailureKind expectedKind, bool retryable)
    {
        var sender = CreateSender(
            new EmailOptions { DeliveryMode = "resend", ApiKey = "test-key", From = "portal@example.test" },
            _ => new HttpResponseMessage(statusCode));

        var result = await sender.SendAsync(Message("outbox/reminder/456"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.ProviderMessageId);
        Assert.Equal(expectedKind, result.Failure!.Kind);
        Assert.Equal(retryable, result.Failure.IsRetryable);
    }

    [Fact]
    public async Task NetworkFailure_ReturnsRetryableResultInsteadOfThrowing()
    {
        var sender = CreateSender(
            new EmailOptions { DeliveryMode = "resend", ApiKey = "test-key", From = "portal@example.test" },
            _ => throw new HttpRequestException("test transport failure"));

        var result = await sender.SendAsync(Message("outbox/reminder/456"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(EmailFailureKind.Network, result.Failure!.Kind);
        Assert.True(result.Failure.IsRetryable);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ControlledResendDelivery_ReturnsProviderMessageId()
    {
        var apiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        var from = Environment.GetEnvironmentVariable("EMAIL_FROM");
        var recipient = Environment.GetEnvironmentVariable("RESEND_TEST_RECIPIENT");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(recipient))
        {
            // This test is intentionally inert outside an operator-invoked, controlled delivery run.
            return;
        }

        var sender = new ResendEmailSender(
            new DefaultHttpClientFactory(),
            Options.Create(new EmailOptions { DeliveryMode = "resend", ApiKey = apiKey, From = from }),
            NullLogger<ResendEmailSender>.Instance);

        var result = await sender.SendAsync(
            new EmailMessage(recipient, "Patient portal delivery check", "<p>Delivery check.</p>", $"local-delivery-check/{Guid.NewGuid():N}", "Delivery check."),
            CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new XunitException($"Resend delivery check failed: {result.Failure!.Kind} ({result.Failure.Code}), retryable={result.Failure.IsRetryable}.");
        }

        Assert.False(string.IsNullOrWhiteSpace(result.ProviderMessageId));
    }

    private static EmailMessage Message(string idempotencyKey) => new(
        "recipient@example.test",
        "Portal notification",
        "<p>Your secure link is ready.</p>",
        idempotencyKey,
        "Your secure link is ready.");

    private static ResendEmailSender CreateSender(EmailOptions options, Func<HttpRequestMessage, HttpResponseMessage>? responder = null) => new(
        new StubHttpClientFactory(responder),
        Options.Create(options),
        NullLogger<ResendEmailSender>.Instance);

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage>? responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.resend.com/")
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder?.Invoke(request) ?? JsonResponse(HttpStatusCode.OK, "{\"id\":\"email_default\"}"));
    }

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri("https://api.resend.com/") };
    }
}
