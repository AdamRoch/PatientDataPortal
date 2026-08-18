using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Email;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class EmailOutboxBackgroundServiceTests
{
    [Fact]
    public async Task StartAsync_ProcessesImmediatelyWhenEnabled()
    {
        var processor = new SignalingProcessor();
        await using var provider = Services(processor).BuildServiceProvider();
        using var service = CreateService(provider);

        await service.StartAsync(CancellationToken.None);
        await processor.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task ProcessOnce_ResolvesAScopedProcessorAndReturnsItsResult()
    {
        var processor = new RecordingProcessor(new EmailOutboxRunResult());
        await using var provider = Services(processor).BuildServiceProvider();
        var service = CreateService(provider);

        var result = await service.ProcessOnceAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task ProcessOnce_ContainsFailuresSoTheNextPollCanContinue()
    {
        var processor = new ThrowingProcessor();
        await using var provider = Services(processor).BuildServiceProvider();
        var service = CreateService(provider);

        var result = await service.ProcessOnceAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, processor.Calls);
    }

    private static ServiceCollection Services(IEmailOutboxProcessor processor) => new ServiceCollection()
        .AddScoped<IEmailOutboxProcessor>(_ => processor);

    private static EmailOutboxBackgroundService CreateService(IServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new OutboxOptions { BackgroundProcessingEnabled = true, PollSeconds = 1 }),
        NullLogger<EmailOutboxBackgroundService>.Instance);

    private sealed class RecordingProcessor(EmailOutboxRunResult result) : IEmailOutboxProcessor
    {
        public int Calls { get; private set; }

        public Task<EmailOutboxRunResult> ProcessAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingProcessor : IEmailOutboxProcessor
    {
        public int Calls { get; private set; }

        public Task<EmailOutboxRunResult> ProcessAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Synthetic worker failure.");
        }
    }

    private sealed class SignalingProcessor : IEmailOutboxProcessor
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public Task<EmailOutboxRunResult> ProcessAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Called.TrySetResult();
            return Task.FromResult(new EmailOutboxRunResult());
        }
    }
}
