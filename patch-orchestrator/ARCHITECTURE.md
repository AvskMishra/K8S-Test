# Patch Orchestrator — Architecture Overview

A folder-by-folder, file-by-file map of what this app does. For *why* things were built this way,
see [PLAN.md](PLAN.md); for *how to run it*, see [README.md](README.md). This doc is just: what
lives where, and what it's responsible for.

## Folder structure

```
patch-orchestrator/
├── PatchOrchestrator/            the .NET application itself
│   ├── Models/                   plain data shapes - no logic
│   ├── Services/                 all the actual logic and Kubernetes calls
│   ├── Worker.cs                 the background loop that ties it together
│   ├── Program.cs                startup wiring
│   ├── appsettings.json          configuration
│   └── data/                     the input file + the generated state file
├── deploy/
│   └── rbac.yaml                 permissions this app needs against the cluster
├── PLAN.md                       design reasoning and decisions
└── README.md                     how to run it
```

---

## `PatchOrchestrator/Models/` — the data shapes

Nothing in this folder talks to Kubernetes or does any work. It just defines the shape of the
data everything else passes around.

- **`PatchScheduleState.cs`** — the enum every schedule moves through:
  `Pending → Draining → ReadyForPatch → WaitingForReboot → Completed`, with `NeedsAttention` as a
  side-branch reachable whenever something needs a human. This is the single source of truth for
  "what stage is this patch job at".

- **`PatchSchedule.cs`** — one row of `data/patch-schedule.json`. `ServerName`, `Date`, `Time` are
  the only fields a person/external system is meant to fill in; `State`, `BootIdAtDrainComplete`,
  and `History` are written by the app itself as it works through the schedule. Also exposes
  `PatchAtLocal`, which just combines `Date` + `Time` into one comparable timestamp.

- **`NodeStateSnapshot.cs`** — one row of `data/node-state.json`. A point-in-time snapshot of a
  single node: is it Ready, is it cordoned, how many pods are running on it, and whether a patch
  workflow is currently active for it.

---

## `PatchOrchestrator/Services/` — where everything actually happens

- **`KubeClientFactory.cs`** — builds the one `IKubernetes` client the whole app shares, from a
  kubeconfig file. This is the *only* place that decides how the app authenticates to the
  cluster; everything else just receives an already-built client.

- **`ScheduleStore.cs`** — reads and writes `data/patch-schedule.json`. Nobody else touches that
  file directly. `LoadAllAsync()` returns the current list of schedules (empty list if the file
  is missing or broken - never throws). `SaveAllAsync()` overwrites the file with updated state.
  A `SemaphoreSlim` stops the load-at-start / save-at-end of one tick from ever overlapping badly.

- **`NodePatchOperations.cs`** — every direct Kubernetes API call this app makes, in one place:
  - `CordonAsync` / `UncordonAsync` — flip `spec.unschedulable` on a node.
  - `AddPatchTaintAsync` / `RemovePatchTaintAsync` — add/remove this app's own taint, without
    touching any other taints already on the node.
  - `GetMigratablePodsAsync` — lists the pods on a node that are safe to evict (skips DaemonSet
    pods, static/mirror pods, bare pods with no controller, and anything already finished).
  - `GetBarePodsAsync` — separately reports pods with no owning controller, so they can be
    flagged instead of silently ignored.
  - `TryEvictPodAsync` — gracefully evicts one pod via the Kubernetes Eviction API (the same
    mechanism `kubectl drain` uses); returns `false` instead of throwing if a
    PodDisruptionBudget blocks it.
  - `TryGetNodeAsync`, `IsReady`, `GetBootId` — read a node's live status, including the boot ID
    used to detect a real reboot.

- **`PatchWorkflowEngine.cs`** — the "brain". Given one schedule and the current time, decides
  what single next step is due and performs it, by calling into `NodePatchOperations`. This is
  where the state machine transitions actually happen (`StartDrainingAsync`,
  `ContinueDrainingAsync`, `FinishDrainingAsync`, `CheckForRebootAndRestoreAsync`). It never loops
  or blocks waiting for hours - it's called once per schedule, every tick, and always returns
  quickly.

- **`NodeStateReporter.cs`** — independent of any specific schedule. On every tick, reads every
  node's live status plus every pod's placement, and writes the full picture to
  `data/node-state.json`. This is what answers "what's the state of every server right now",
  whether or not that server has a patch scheduled.

---

## `PatchOrchestrator/Worker.cs` — the loop that ties it together

The only `BackgroundService` in the app. On a `PeriodicTimer` (`CheckIntervalMinutes`, default 5):

1. Load all schedules (`ScheduleStore.LoadAllAsync`).
2. For each schedule, ask the workflow engine to advance it one step
   (`PatchWorkflowEngine.ProcessAsync`) - wrapped in its own try/catch, so one node's problem
   can't stop other nodes' schedules from progressing in the same tick.
3. Save all schedules back (`ScheduleStore.SaveAllAsync`) - persists whatever state changed.
4. Write a fresh node-state snapshot (`NodeStateReporter.WriteSnapshotAsync`).

An outer try/catch around the whole tick means even a totally unreachable cluster just gets
logged and retried on the next tick - the service itself never crashes.

---

## `PatchOrchestrator/Program.cs` — startup wiring

Builds the generic host, registers one shared `IKubernetes` client plus each service above as a
singleton, and registers `Worker` as the hosted background service. No web server is started -
this app has no HTTP endpoints in this increment.

---

## `PatchOrchestrator/appsettings.json` — configuration

Every tunable number lives under the `PatchOrchestrator` section: which kubeconfig to use, where
the two data files live, how often to check, how far ahead of patch time to start draining, how
long to wait for a reboot before giving up, and how the eviction pacing (`MaxEvictionsPerTick`,
`SecondsBetweenEvictions`) is tuned. See README.md for the full table with defaults.

---

## `PatchOrchestrator/data/` — the two JSON files

- **`patch-schedule.sample.json`** (committed) — shows the exact input shape to copy.
- **`patch-schedule.json`** (gitignored, created on first save) — the real input **and** the
  app's own progress notes for each schedule (`State`, `History`, `BootIdAtDrainComplete`).
- **`node-state.json`** (gitignored, created on first successful cluster read) — rewritten every
  tick; the current Ready/cordoned/pod-count snapshot of every node in the cluster.

---

## `deploy/rbac.yaml`

The minimal `ClusterRole` this app's identity needs: `get`/`list`/`patch` on `nodes`,
`get`/`list` on `pods`, and `create` on `pods/eviction` only (never plain pod `delete` - eviction
is the only removal path this app has, so it can never do a sudden/ungraceful kill).

---

## End-to-end: one tick, in order

```
Worker timer fires
   │
   ▼
ScheduleStore.LoadAllAsync()  ──►  data/patch-schedule.json
   │
   ▼
for each schedule:
   PatchWorkflowEngine.ProcessAsync(schedule)
       │
       ├─ Pending          → still waiting, or CordonAsync + move to Draining
       ├─ Draining         → GetMigratablePodsAsync + TryEvictPodAsync (a few at a time)
       ├─ ReadyForPatch    → wait for patch time, then move to WaitingForReboot
       ├─ WaitingForReboot → TryGetNodeAsync, compare BootID, Uncordon+RemoveTaint if rebooted
       └─ Completed / NeedsAttention → no action
   │
   ▼
ScheduleStore.SaveAllAsync(schedules)  ──►  data/patch-schedule.json (updated)
   │
   ▼
NodeStateReporter.WriteSnapshotAsync(schedules)  ──►  data/node-state.json (fresh snapshot)
```
