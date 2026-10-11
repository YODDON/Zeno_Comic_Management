using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicManagement.Persistence;

internal sealed class OutboxWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimedCount = 0;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
                var result = await processor.ProcessBatchAsync(stoppingToken);
                claimedCount = result.ClaimedCount;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Outbox worker batch failed with {ErrorType}; polling will resume.",
                    exception.GetType().Name);
            }

            if (claimedCount >= options.Value.BatchSize)
            {
                continue;
            }

            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}
