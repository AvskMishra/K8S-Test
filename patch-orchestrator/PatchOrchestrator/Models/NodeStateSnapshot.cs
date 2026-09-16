namespace PatchOrchestrator.Models;

// One row of data/node-state.json - a fresh, at-a-glance snapshot of one node, rewritten every
// check (default every 5 minutes). This is the answer to "what's the current state of the
// servers in this cluster right now", combining live facts from the Kubernetes API with
// whichever patch workflow (if any) is currently active for that node.
public class NodeStateSnapshot
{
    public string NodeName { get; set; } = string.Empty;

    // From the node's own "Ready" condition - is Kubernetes able to schedule work on it at all.
    public bool Ready { get; set; }

    // True while cordoned (no new pods can land here) - either because a patch window is open,
    // or because someone cordoned it by hand outside this app.
    public bool Unschedulable { get; set; }

    public int TaintCount { get; set; }

    public int RunningPodCount { get; set; }

    // Null when no patch schedule currently applies to this node. Otherwise the PatchScheduleState
    // of whichever schedule is active (see PatchScheduleState.cs).
    public string? ActivePatchState { get; set; }

    public DateTime LastCheckedUtc { get; set; }
}
