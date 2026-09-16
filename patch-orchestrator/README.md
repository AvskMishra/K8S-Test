# Patch Orchestrator

A .NET background service that prepares a Kubernetes node for a scheduled OS patch + reboot done
by a 3rd party, and restores it afterwards. See [PLAN.md](PLAN.md) for the full design reasoning.

## What it does, in order

1. You (or an external system) add an entry to `data/patch-schedule.json`: which node, and when.
2. Every 5 minutes, the app checks all schedules. Once a schedule is within 2 hours of its patch
   time, it **cordons** the node (no new pods) and starts **gracefully evicting** the pods already
   there, a few at a time, so they get rescheduled onto other nodes.
3. Once every migratable pod is off the node (or the 2-hour window runs out), it **taints** the
   node as a second confirmation it's ready — this is the point where the 3rd party can safely
   patch and reboot it. The app does not do the patch or reboot itself.
4. After the scheduled time, the app watches the node's Kubernetes "boot ID". Once that changes
   (proof the reboot actually happened) and the node reports `Ready` again, it automatically
   **uncordons** the node and **removes the taint** — the node is back in normal service.
5. Separately, on every 5-minute check, it writes `data/node-state.json` — a snapshot of every
   node in the cluster (Ready?, cordoned?, pod count, any active patch state), so you always know
   where every server currently stands.

## Running it

```powershell
cd patch-orchestrator/PatchOrchestrator
dotnet run
```

### 1. Point it at your cluster

Edit `appsettings.json` → `PatchOrchestrator:KubeconfigPath`. Leave it empty to use the same
default `kubectl` itself uses (`~/.kube/config`). For this repo's WSL2 lab cluster specifically,
point it at the same kubeconfig `k8s-console-tool` already generated:
`../../k8s-console-tool/kubeconfig-direct.yaml` (that tunnel must be running first — see that
project's README for `start-api-tcp-tunnel.sh`).

The identity in that kubeconfig needs the permissions listed in
[`deploy/rbac.yaml`](../deploy/rbac.yaml) — get a cluster admin to apply that `ClusterRole` +
`ClusterRoleBinding` (adjusted to name the real user/cert) if you're not already cluster-admin.

### 2. Add a schedule

Copy the sample and edit it:

```powershell
Copy-Item data/patch-schedule.sample.json data/patch-schedule.json
```

```json
[
  {
    "ServerName": "server01",
    "Date": "2026-09-12",
    "Time": "10:00"
  }
]
```

`ServerName` must exactly match a node name from `kubectl get nodes`. Only these three fields are
yours to set — the app adds `State`, `History`, and `BootIdAtDrainComplete` itself as it works
through the schedule, and rewrites the file after every check.

### 3. Watch it work

```powershell
dotnet run
```

Console output logs every action (cordon, each eviction, taint, uncordon). Between runs, the two
JSON files under `data/` tell the whole story:
- `data/patch-schedule.json` — `History` on each entry is a plain-English timeline.
- `data/node-state.json` — current Ready/cordoned/pod-count snapshot of every node, refreshed
  every check.

## Configuration reference (`appsettings.json` → `PatchOrchestrator`)

| Key | Default | Meaning |
|---|---|---|
| `KubeconfigPath` | *(empty)* | Path to the cluster's kubeconfig; empty = `~/.kube/config` |
| `SchedulesFilePath` | `data/patch-schedule.json` | The input/state file described above |
| `NodeStateFilePath` | `data/node-state.json` | The always-fresh node snapshot file |
| `CheckIntervalMinutes` | `5` | How often the background loop wakes up |
| `DrainLeadTimeHours` | `2` | How long before patch time to cordon + start draining |
| `MaxWaitForRebootHours` | `6` | Give up auto-restoring and flag for a human after this long |
| `SecondsBetweenEvictions` | `15` | Pause after each pod eviction, so it lands elsewhere before the next one goes |
| `MaxEvictionsPerTick` | `3` | Pods evicted per check — paces draining across the whole window |

## Not done yet / known gaps

- Not yet run against a live cluster (see PLAN.md §"Progress log" — the lab cluster was offline
  when this was built). Next step once it's up: schedule a real node a few minutes out, watch it
  cordon/drain/taint for real, then manually reboot that node (simulating the 3rd party) and watch
  the app auto-restore it.
- Bare pods (no Deployment/StatefulSet/etc. owning them) are deliberately left alone rather than
  deleted — flagged in `History` instead. If a workload runs as bare pods, that needs a person.
- No UI. `data/node-state.json` and each schedule's `History` are the only "dashboard" today; a
  natural next step is surfacing them in `k8s-dashboard` if that's useful.
