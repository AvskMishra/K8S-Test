using PatchOrchestrator.Models;

namespace PatchOrchestrator.Services;

// The "brain" of the app. Given one patch schedule and the current time, this decides what (if
// anything) needs to happen next, does it, and updates the schedule in place - ready for
// Worker.cs to save back to disk.
//
// It is a plain state machine driven purely by "where are we relative to the schedule's patch
// time" plus live signals read back from the cluster (has the node finished draining? has it
// rebooted yet?). Nothing here runs on its own internal timer - Worker.cs calls ProcessAsync
// once per schedule, every tick (every CheckIntervalMinutes, default 5), and this class only
// ever performs the one next step that's due right now. See PatchScheduleState.cs for the full
// list of states this walks through, in order.
public class PatchWorkflowEngine
{
    private readonly NodePatchOperations _nodeOps;
    private readonly ILogger<PatchWorkflowEngine> _logger;
    private readonly TimeSpan _drainLeadTime;
    private readonly TimeSpan _maxWaitForReboot;
    private readonly TimeSpan _pauseBetweenEvictions;

    // How many pods to evict in a single tick, rather than draining the whole node in one go.
    // This is what actually makes the "spread pods evenly across other nodes, not 5-5 on 2 nodes"
    // requirement hold up in practice: evict a small batch, give the cluster a short pause to
    // place their replacements (Kubernetes' scheduler scores nodes and favours the
    // least-loaded ones, so a paced trickle spreads out naturally rather than racing to whichever
    // node responds first), then pick up the rest on the next tick - spread over the whole
    // drain-lead-time window, not all at once.
    private readonly int _maxEvictionsPerTick;

    public PatchWorkflowEngine(NodePatchOperations nodeOps, ILogger<PatchWorkflowEngine> logger, IConfiguration configuration)
    {
        _nodeOps = nodeOps;
        _logger = logger;
        _drainLeadTime = TimeSpan.FromHours(configuration.GetValue("PatchOrchestrator:DrainLeadTimeHours", 2.0));
        _maxWaitForReboot = TimeSpan.FromHours(configuration.GetValue("PatchOrchestrator:MaxWaitForRebootHours", 6.0));
        _pauseBetweenEvictions = TimeSpan.FromSeconds(configuration.GetValue("PatchOrchestrator:SecondsBetweenEvictions", 15));
        _maxEvictionsPerTick = configuration.GetValue("PatchOrchestrator:MaxEvictionsPerTick", 3);
    }

    public async Task ProcessAsync(PatchSchedule schedule)
    {
        var now = DateTime.Now;
        DateTime patchAt;
        try
        {
            patchAt = schedule.PatchAtLocal;
        }
        catch (FormatException)
        {
            schedule.State = PatchScheduleState.NeedsAttention;
            Note(schedule, $"Could not parse Date '{schedule.Date}' / Time '{schedule.Time}' - expected 'yyyy-MM-dd' and 'HH:mm'.");
            return;
        }

        var drainWindowOpensAt = patchAt - _drainLeadTime;

        switch (schedule.State)
        {
            case PatchScheduleState.Pending:
                if (now >= drainWindowOpensAt)
                {
                    await StartDrainingAsync(schedule);
                }
                break; // still outside the lead window - nothing to do yet

            case PatchScheduleState.Draining:
                await ContinueDrainingAsync(schedule, patchAt);
                break;

            case PatchScheduleState.ReadyForPatch:
                if (now >= patchAt)
                {
                    schedule.State = PatchScheduleState.WaitingForReboot;
                    Note(schedule, $"Patch time ({patchAt:yyyy-MM-dd HH:mm}) reached - handing the node to the 3rd party for patch + reboot. Watching for a new boot ID.");
                }
                break;

            case PatchScheduleState.WaitingForReboot:
                await CheckForRebootAndRestoreAsync(schedule, patchAt);
                break;

            case PatchScheduleState.Completed:
            case PatchScheduleState.NeedsAttention:
                break; // terminal, or waiting on a human - this app does not act further on its own
        }
    }

    private async Task StartDrainingAsync(PatchSchedule schedule)
    {
        var node = await _nodeOps.TryGetNodeAsync(schedule.ServerName);
        if (node is null)
        {
            schedule.State = PatchScheduleState.NeedsAttention;
            Note(schedule, $"Node '{schedule.ServerName}' was not found in the cluster - check the name matches `kubectl get nodes` exactly.");
            return;
        }

        await _nodeOps.CordonAsync(schedule.ServerName);
        schedule.State = PatchScheduleState.Draining;
        Note(schedule, $"Entered the drain window ({_drainLeadTime.TotalHours}h before patch time). Node cordoned - no new pods will land here. Starting graceful pod migration.");
    }

    private async Task ContinueDrainingAsync(PatchSchedule schedule, DateTime patchAt)
    {
        var migratable = await _nodeOps.GetMigratablePodsAsync(schedule.ServerName);

        if (migratable.Count == 0)
        {
            await FinishDrainingAsync(schedule);
            return;
        }

        // Evict a small, paced batch this tick - see the class comment on _maxEvictionsPerTick.
        var evictedCount = 0;
        foreach (var pod in migratable.Take(_maxEvictionsPerTick))
        {
            var accepted = await _nodeOps.TryEvictPodAsync(pod);
            if (accepted)
            {
                evictedCount++;
                await Task.Delay(_pauseBetweenEvictions);
            }
        }

        Note(schedule, $"Drain in progress: {evictedCount} pod(s) evicted this check, {migratable.Count - evictedCount} remaining or currently blocked.");

        // Time's up: move whatever we could gracefully move, flag whatever's still stuck, but
        // still hand the node over on schedule - this app's job is to get workloads out of the
        // way, not to delay the 3rd party's patch window indefinitely.
        if (DateTime.Now >= patchAt)
        {
            var stillRemaining = await _nodeOps.GetMigratablePodsAsync(schedule.ServerName);
            if (stillRemaining.Count > 0)
            {
                await _nodeOps.AddPatchTaintAsync(schedule.ServerName);
                schedule.State = PatchScheduleState.NeedsAttention;
                Note(schedule, $"Patch time reached but {stillRemaining.Count} pod(s) could not be gracefully evicted " +
                    "(most likely blocked by a PodDisruptionBudget). Node is cordoned and tainted regardless; review these pods manually before the patch proceeds.");
            }
            else
            {
                await FinishDrainingAsync(schedule);
            }
        }
    }

    private async Task FinishDrainingAsync(PatchSchedule schedule)
    {
        var barePods = await _nodeOps.GetBarePodsAsync(schedule.ServerName);
        if (barePods.Count > 0)
        {
            Note(schedule, $"Note: {barePods.Count} pod(s) with no owning controller are still on this node " +
                "and were intentionally left alone (nothing would recreate them elsewhere) - review manually if they matter.");
        }

        await _nodeOps.AddPatchTaintAsync(schedule.ServerName);

        var node = await _nodeOps.TryGetNodeAsync(schedule.ServerName);
        schedule.BootIdAtDrainComplete = node is null ? null : NodePatchOperations.GetBootId(node);

        schedule.State = PatchScheduleState.ReadyForPatch;
        Note(schedule, "All migratable pods evicted. Node cordoned and tainted - safe for the 3rd party to patch and reboot.");
    }

    private async Task CheckForRebootAndRestoreAsync(PatchSchedule schedule, DateTime patchAt)
    {
        var node = await _nodeOps.TryGetNodeAsync(schedule.ServerName);
        if (node is null)
        {
            return; // API server can't see the node right now (e.g. mid-reboot) - just check again next tick
        }

        var currentBootId = NodePatchOperations.GetBootId(node);
        var rebooted = !string.IsNullOrEmpty(schedule.BootIdAtDrainComplete)
            && !string.IsNullOrEmpty(currentBootId)
            && currentBootId != schedule.BootIdAtDrainComplete;

        if (rebooted && NodePatchOperations.IsReady(node))
        {
            await _nodeOps.RemovePatchTaintAsync(schedule.ServerName);
            await _nodeOps.UncordonAsync(schedule.ServerName);
            schedule.State = PatchScheduleState.Completed;
            Note(schedule, "Reboot detected (new boot ID) and the node is Ready again. Taint removed, node uncordoned - back in the normal scheduling pool.");
            return;
        }

        if (DateTime.Now - patchAt > _maxWaitForReboot)
        {
            schedule.State = PatchScheduleState.NeedsAttention;
            Note(schedule, $"No reboot observed within {_maxWaitForReboot.TotalHours}h of the scheduled patch time - " +
                "not auto-restoring the node. Confirm the patch actually happened, then uncordon/untaint it manually.");
        }
    }

    private static void Note(PatchSchedule schedule, string message) =>
        schedule.History.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{schedule.State}] {message}");
}
