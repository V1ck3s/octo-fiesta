using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services;

/// <summary>
/// Background service that periodically refreshes instance speed tests.
/// </summary>
public class InstanceRefreshHostedService : BackgroundService
{
    private readonly InstanceManager _instanceManager;
    private readonly InstanceOptions _options;
    private readonly ILogger<InstanceRefreshHostedService> _logger;

    /// <summary>
    /// Event raised when a refresh is triggered externally.
    /// Implemented with TaskCompletionSource for async-friendly waiting.
    /// </summary>
    private TaskCompletionSource<bool> _refreshTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceRefreshHostedService"/> class.
    /// </summary>
    public InstanceRefreshHostedService(
        InstanceManager instanceManager,
        IOptions<InstanceOptions> options,
        ILogger<InstanceRefreshHostedService> logger)
    {
        _instanceManager = instanceManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Triggers an immediate refresh of all speed tests.
    /// </summary>
    public void TriggerRefresh()
    {
        // Swap in a fresh TCS and complete the previous one so any awaiters wake up
        var tcs = Interlocked.Exchange(ref _refreshTrigger, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.TrySetResult(true);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Instance refresh service started with interval {Interval}ms", _options.RefreshIntervalMs);

        // Initial load/test on startup
        try
        {
            await _instanceManager.RefreshAllAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform initial instance refresh");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the interval, a manual trigger, or cancellation using Task.WhenAny (async-friendly)
                var delayTask = Task.Delay(_options.RefreshIntervalMs, stoppingToken);
                var triggerTask = _refreshTrigger.Task;

                var completed = await Task.WhenAny(delayTask, triggerTask);

                // If cancellation was requested, exit the loop before doing more work
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // If the trigger task completed, a manual refresh was requested
                if (completed == triggerTask)
                {
                    _logger.LogInformation("Manual refresh triggered");
                }

                await _instanceManager.RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during instance refresh");
            }
        }

        _logger.LogInformation("Instance refresh service stopped");
    }

}
