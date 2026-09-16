namespace PatchOrchestrator.Models;

// The lifecycle a single patch schedule moves through. Always moves forward (top to bottom),
// never backward - the only exception is NeedsAttention, which can be reached from more than
// one place when something needs a human to look at it.
public enum PatchScheduleState
{
    // More than DrainLeadTimeHours away from the patch time. No action taken yet.
    Pending,

    // Inside the lead window: node has been cordoned (no new pods) and this app is evicting
    // the node's existing pods a few at a time, spreading migrations out over the window.
    Draining,

    // Every migratable pod is off the node (or the window ran out - see NeedsAttention below).
    // Node is cordoned AND tainted. Safe for the 3rd party to patch and reboot it now.
    ReadyForPatch,

    // The scheduled patch time has arrived. Waiting for the node to actually reboot (detected
    // by its Kubernetes "boot ID" changing) before touching it again.
    WaitingForReboot,

    // Reboot was observed and the node reported Ready again - cordon and taint were removed,
    // the node is back in the normal scheduling pool. Terminal state.
    Completed,

    // Something did not go to plan (node name not found, pods couldn't be evicted in time,
    // reboot never observed within the configured wait window, ...). This app stops guessing
    // and stops acting automatically - check the schedule's History for what happened and why.
    NeedsAttention
}
