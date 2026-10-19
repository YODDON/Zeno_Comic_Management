using ComicManagement.Missions.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicManagement.Persistence;

public sealed class MissionReceiptOptions
{
    public const string SectionName = "Missions:ReceiptWorker";

    public int BatchSize { get; set; } = 20;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

internal sealed class MissionReceiptWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MissionReceiptOptions> options,
    ILogger<MissionReceiptWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                for (; processed < options.Value.BatchSize; processed++)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider
                        .GetRequiredService<MissionReceiptProcessor>();
                    if (await processor.ProcessNextAsync(stoppingToken) ==
                        MissionReceiptProcessingOutcome.NoWork)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Mission receipt processing failed with {ErrorType}; retry remains pending.",
                    exception.GetType().Name);
            }

            if (processed >= options.Value.BatchSize)
            {
                continue;
            }

            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}
