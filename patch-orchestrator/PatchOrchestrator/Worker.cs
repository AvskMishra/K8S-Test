using PatchOrchestrator.Services;

namespace PatchOrchestrator;

// The whole app is one loop: every CheckIntervalMinutes (default 5), load the current
// schedules, move each one forward by whatever step is due, save the results back, then
// snapshot every node's state. No web server, no database - a simple background service, as asked.
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ScheduleStore _scheduleStore;
    private readonly PatchWorkflowEngine _workflowEngine;
    private readonly NodeStateReporter _stateReporter;
    private readonly TimeSpan _checkInterval;

    public Worker(
        ILogger<Worker> logger,
        ScheduleStore scheduleStore,
        PatchWorkflowEngine workflowEngine,
        NodeStateReporter stateReporter,
        IConfiguration configuration)
    {
        _logger = logger;
        _scheduleStore = scheduleStore;
        _workflowEngine = workflowEngine;
        _stateReporter = stateReporter;
        _checkInterval = TimeSpan.FromMinutes(configuration.GetValue("PatchOrchestrator:CheckIntervalMinutes", 5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Patch Orchestrator starting - checking every {Minutes} minute(s)", _checkInterval.TotalMinutes);

        using var timer = new PeriodicTimer(_checkInterval);

        // Run once immediately on startup (so you don't wait a full interval to see it work),
        // then again every time the timer ticks.
        do
        {
            await RunOneCheckAsync();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOneCheckAsync()
    {
        try
        {
            var schedules = await _scheduleStore.LoadAllAsync();
            _logger.LogInformation("Checking {Count} patch schedule(s)", schedules.Count);

            // Each schedule gets its own try/catch: one node having a bad tick (an unreachable
            // API call, an unexpected error) should not stop unrelated schedules for other nodes
            // from being processed and saved in the same run.
            foreach (var schedule in schedules)
            {
                try
                {
                    await _workflowEngine.ProcessAsync(schedule);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Processing schedule for {ServerName} failed - will retry on the next check", schedule.ServerName);
                }
            }

            await _scheduleStore.SaveAllAsync(schedules);
            await _stateReporter.WriteSnapshotAsync(schedules);
        }
        catch (Exception ex)
        {
            // Anything outside the per-schedule loop (e.g. the schedule file itself is
            // unreadable, or writing the node-state snapshot failed) should still never take down
            // the whole background service - log it and simply try again on the next tick.
            _logger.LogError(ex, "A scheduled check failed - will retry on the next tick");
        }
    }
}
