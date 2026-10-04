using Microsoft.Extensions.Options;

namespace Atlas.Verification.Worker;

public sealed class VerificationWorker(
    VerificationProcessor processor,
    IOptions<WorkerOptions> options,
    ILogger<VerificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Verification worker {Instance} started", processor.InstanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.RunOnceAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(options.Value.PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Verification sweep failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
