using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Email;

public sealed class ResendEmailSender(
    IHttpClientFactory httpClientFactory,
    IOptions<EmailOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    private const string ResendClientName = "resend";

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.IdempotencyKey))
        {
            return Failure(EmailFailureKind.Rejected, "missing_idempotency_key", false);
        }

        if (message.IdempotencyKey.Length > 256)
        {
            return Failure(EmailFailureKind.Rejected, "invalid_idempotency_key", false);
        }

        var emailOptions = options.Value;
        if (string.Equals(emailOptions.DeliveryMode, "log", StringComparison.OrdinalIgnoreCase))
        {
            var messageId = $"dev_{message.IdempotencyKey}";
            logger.LogInformation("Email accepted in log delivery mode {ProviderMessageId} {IdempotencyKey}", messageId, message.IdempotencyKey);
            return EmailSendResult.Sent(messageId);
        }

        if (!string.Equals(emailOptions.DeliveryMode, "resend", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(EmailFailureKind.Configuration, "invalid_email_delivery_mode", false);
        }

        if (string.IsNullOrWhiteSpace(emailOptions.ApiKey) || string.IsNullOrWhiteSpace(emailOptions.From))
        {
            return Failure(EmailFailureKind.Configuration, "resend_not_configured", false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new
            {
                from = emailOptions.From,
                to = new[] { message.To },
                subject = message.Subject,
                html = message.Html,
                text = message.Text
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", emailOptions.ApiKey);
        request.Headers.Add("Idempotency-Key", message.IdempotencyKey);

        try
        {
            using var response = await httpClientFactory.CreateClient(ResendClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ToFailure(response.StatusCode);
                logger.LogWarning("Resend rejected email {ErrorCode} {Retryable} {StatusCode}", failure.Code, failure.IsRetryable, (int)response.StatusCode);
                return EmailSendResult.Failed(failure);
            }

            var body = await response.Content.ReadFromJsonAsync<ResendSendResponse>(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(body?.Id))
            {
                return Failure(EmailFailureKind.InvalidProviderResponse, "resend_missing_message_id", true);
            }

            logger.LogInformation("Resend accepted email {ProviderMessageId} {IdempotencyKey}", body.Id, message.IdempotencyKey);
            return EmailSendResult.Sent(body.Id);
        }
        catch (HttpRequestException)
        {
            return Failure(EmailFailureKind.Network, "resend_network_error", true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(EmailFailureKind.Network, "resend_timeout", true);
        }
    }

    private EmailSendResult Failure(EmailFailureKind kind, string code, bool retryable)
    {
        logger.LogWarning("Email send failed {ErrorCode} {Retryable}", code, retryable);
        return EmailSendResult.Failed(new EmailSendFailure(kind, code, retryable));
    }

    private static EmailSendFailure ToFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => new(EmailFailureKind.RateLimited, "resend_rate_limited", true),
        HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            => new(EmailFailureKind.ProviderUnavailable, $"resend_http_{(int)statusCode}", true),
        _ => new(EmailFailureKind.Rejected, $"resend_http_{(int)statusCode}", false)
    };

    private sealed record ResendSendResponse(string? Id);
}
