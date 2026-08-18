using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Email;

public sealed class EmailOutboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<EmailOutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.BackgroundProcessingEnabled)
        {
            logger.LogInformation("Email outbox background processing is disabled");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.PollSeconds));
        logger.LogInformation("Email outbox background processing started {PollSeconds}", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessOnceAsync(stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    internal async Task<EmailOutboxRunResult?> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IEmailOutboxProcessor>();
            var result = await processor.ProcessAsync(cancellationToken);
            if (result.Claimed > 0)
            {
                logger.LogInformation(
                    "Email outbox background run completed {Claimed} {Sent} {Failed} {Superseded}",
                    result.Claimed,
                    result.Sent,
                    result.Failed,
                    result.Superseded);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Email outbox background run failed");
            return null;
        }
    }
}
