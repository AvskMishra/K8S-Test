# Manual Testing Guide

A hands-on walkthrough for testing Patch Orchestrator yourself against the original scenario —
"server01 needs a patch at 10 AM, node should stop taking new pods 2 hours before, all pods should
migrate gracefully and spread evenly, node should be cordoned+tainted by patch time" — and, just
as importantly, exactly how to *watch it happen* rather than just trust that it happened.

Written assuming the same `k8slab` lab cluster used in [TEST-REPORT.md](TEST-REPORT.md) (minikube,
inside this repo's WSL2 Ubuntu distro), but every `kubectl` command here works against any cluster.
If your cluster runs inside this repo's WSL2 setup, prefix each `kubectl ...` command with:
```bash
wsl.exe -d Ubuntu -u root -- sh -c "kubectl ..."
```
If you have `kubectl` directly on your PATH (a real cluster, or a properly configured
`KUBECONFIG`), just run the commands as shown.

---

## The setup: open 2-3 terminals

This guide only works if you can watch things update *while* the app is running, so don't run
everything in one terminal:

- **Terminal A** — runs the app (`dotnet run`). Its own console output is your primary log.
- **Terminal B** — runs `kubectl` commands to independently watch the cluster change.
- **Terminal C** (optional but recommended) — tails the two JSON files the app writes.

---

## Step 0 — Confirm the cluster is up and pick a node

```bash
kubectl get nodes -o wide
```
Pick a node that is **not** your control-plane node if you can spare it, so you don't disturb
cluster-critical pods. Note its exact name — it must match exactly in the schedule file.

---

## Step 1 — Give yourself something to migrate

If you already have real workloads spread across nodes, you can skip to Step 2 and just pick a
node that already has a few pods on it. To deliberately set up a clean test instead (recommended
the first time, so you have a predictable "before" picture):

```bash
# Temporarily cordon every node except the one you're about to test,
# so a fresh test Deployment has nowhere else to land.
kubectl cordon <other-node-1> <other-node-2> <other-node-3>

kubectl create namespace patch-test
kubectl -n patch-test create deployment drain-test --image=busybox --replicas=6 -- sh -c 'while true; do sleep 3600; done'

# Wait a few seconds, then confirm all replicas landed on your target node:
kubectl -n patch-test get pods -o wide

# Give the other nodes somewhere to send replacements later:
kubectl uncordon <other-node-1> <other-node-2> <other-node-3>
```

`kubectl -n patch-test get pods -o wide` should now show all 6 pods on your target node and no
others — that's your "before" state.

---

## Step 2 — Write the schedule

Copy the sample and edit it:

```bash
cd patch-orchestrator/PatchOrchestrator
cp data/patch-schedule.sample.json data/patch-schedule.json
```

```json
[
  { "ServerName": "<your-target-node>", "Date": "2026-09-16", "Time": "10:00" }
]
```

**Timing tip for testing (don't wait 2 real hours):** the default `DrainLeadTimeHours` is 2 — the
app only starts acting once `now >= patchTime - 2h`. So instead of shrinking that setting, just
pick a `Date`/`Time` that's a few minutes in the future. Because 2 hours before that is already in
the past, draining starts on the very first tick, no waiting required. For example, if it's
17:39 right now, set `"Time": "17:44"` — the drain window (17:44 minus 2h = 15:44) is already open.

**Speed-up tip (optional):** the default check interval is 5 minutes, which is a long time to sit
and watch. To iterate faster during manual testing, add this to
`appsettings.Development.json` (this file is only used by `dotnet run`'s default environment, so
it's safe to edit for testing and won't affect a real deployment):

```json
{
  "PatchOrchestrator": {
    "CheckIntervalMinutes": 1,
    "SecondsBetweenEvictions": 3
  }
}
```

**Remember to revert `appsettings.Development.json` back to empty after testing** — see Cleanup.

---

## Step 3 — Start the app (Terminal A)

```bash
cd patch-orchestrator/PatchOrchestrator
dotnet run
```

Leave this running and visible — its console output is your first and most detailed source of
truth. You should see, roughly every tick:

```
info: PatchOrchestrator.Worker[0]
      Checking 1 patch schedule(s)
info: PatchOrchestrator.Services.NodePatchOperations[0]
      Cordoned node <node> (marked unschedulable - no new pods)
...
info: PatchOrchestrator.Services.NodeStateReporter[0]
      Node <node>: Ready=True Unschedulable=True Pods=N PatchState=Draining
```

---

## Step 4 — Watch it happen live (Terminal B)

This is the "how do I see the background changes" part — several independent windows into the
same process, so you're never just trusting the app's own log.

### 4.1 Watch pods move, in real time
```bash
kubectl -n patch-test get pods -o wide --watch
```
What to look for, in order:
- Nothing changes for the first tick (cordon only affects *new* pods).
- A pod's `STATUS` flips to `Terminating` — that's an eviction being processed. It stays in this
  state for up to its `terminationGracePeriodSeconds` (default 30s) — this is the "graceful, not
  sudden" behaviour actually happening, not just claimed. Compare against a plain `kubectl delete
  pod --grace-period=0 --force`, which would remove it instantly instead.
- At the same time, a **new** pod for the same Deployment appears `Pending` → `ContainerCreating`
  → `Running`, with a `NODE` column value that's a *different* node than the one being drained.
- Repeat until no pods remain `Running` on the target node (its own DaemonSet pods —
  `kube-proxy`, the CNI plugin — are the only things still shown there; that's correct, not a bug).

### 4.2 Watch the node's schedulability and taints
```bash
kubectl get nodes
```
A cordoned node shows `Ready,SchedulingDisabled` in the `STATUS` column — visible without even
needing `-o wide`.

To see the actual taint appear (Windows has no built-in `watch`; this loops manually):
```powershell
while ($true) {
  kubectl describe node <your-target-node> | Select-String "Taints|Unschedulable"
  Start-Sleep -Seconds 5
}
```
or on the WSL/Linux side:
```bash
watch -n 5 "kubectl describe node <your-target-node> | grep -E 'Taints|Unschedulable'"
```
You should see `Taints: <none>` at first, then (once draining finishes)
`node.kubernetes.io/unschedulable:NoSchedule` (added automatically by the cordon) and
`patch-orchestrator/scheduled-patch=true:NoSchedule` (added by the app itself).

### 4.3 Watch cluster events (the eviction, from Kubernetes' own point of view)
```bash
kubectl get events -n patch-test --sort-by=.lastTimestamp --watch
```
Look for `Killing` (grace period starting) and scheduler `Scheduled` events for the replacement
pods landing on other nodes. If a `PodDisruptionBudget` is ever blocking an eviction, it shows up
here too, before it ever shows up in the app's own log.

### 4.4 Pod logs — watching a pod shut down gracefully
`busybox sleeping` doesn't log anything interesting on shutdown, so for a more realistic test,
deploy something that actually logs, e.g. an nginx pod, and tail it *while* it's being evicted:
```bash
kubectl -n patch-test logs <pod-name> --follow
```
For an app that handles `SIGTERM` (most real services do), you'll see its own shutdown-sequence
log lines appear in this window in the moments before the pod disappears — direct evidence the
eviction gave it a real chance to shut down cleanly, not just cut the process. If you don't have a
real app handy, you can at least confirm the *timing* one of two ways:
```bash
# Note the pod's own grace period:
kubectl -n patch-test get pod <pod-name> -o jsonpath='{.spec.terminationGracePeriodSeconds}'
# Then time how long it stays "Terminating" in the --watch from 4.1 - it should roughly match,
# not disappear instantly.
```

### 4.5 The app's own files (Terminal C) — this is the "state of the whole cluster" view
```bash
cd patch-orchestrator/PatchOrchestrator
```
```powershell
while ($true) { Get-Content data/patch-schedule.json -Raw; Start-Sleep -Seconds 5; Clear-Host }
```
Watch the `History` array grow, one plain-English line per action the app took — this is the exact
same information as the console log, but persisted, and readable without scrolling back through
terminal output.

```powershell
while ($true) { Get-Content data/node-state.json -Raw; Start-Sleep -Seconds 5; Clear-Host }
```
This is the file that answers the original ask's "my app should know the state of all the servers
in the cluster" — rewritten every tick, for *every* node, whether or not it's the one being
patched. Watch the target node's `Unschedulable`/`TaintCount`/`ActivePatchState` fields change
over the same ticks you're watching in `kubectl` — they should always agree.

---

## Step 5 — Verify the specific claims from the original scenario

Now that you've watched it happen, confirm each requirement explicitly:

**"No new pods land on the cordoned node"** — try to force one there and watch it get refused:
```bash
kubectl -n patch-test scale deployment drain-test --replicas=8
kubectl -n patch-test get pods -o wide
```
The 2 new replicas should land on the *other* nodes, never on the cordoned one, even though
Kubernetes would otherwise be free to choose it.

**"Pods spread evenly, not piled onto one or two nodes"**:
```bash
kubectl -n patch-test get pods -o jsonpath='{range .items[*]}{.spec.nodeName}{"\n"}{end}' | sort | uniq -c
```
Counts per node should be roughly equal (within 1) across every remaining node, not lopsided.

**"Node ready for the 3rd party by patch time"**:
```bash
kubectl get node <your-target-node> -o jsonpath='{.spec.unschedulable}{"\n"}{.spec.taints}{"\n"}'
```
Should show `true` and the `patch-orchestrator/scheduled-patch` taint.

**"The app knows the current state of the server"** — check `data/node-state.json` (Step 4.5) or,
equivalently, just ask the cluster the same question the app asks:
```bash
kubectl get node <your-target-node> -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}'
```

---

## Step 6 — The part that needs a real reboot

Once the node is `ReadyForPatch`, this is the point a real 3rd party would patch and reboot the
physical/VM server. If you're testing against real hardware (not a minikube container node), you
can actually do this and watch the full loop close:

1. Reboot the node for real.
2. Watch the app's log — once the node comes back, within one tick you should see:
   ```
   Reboot detected (new boot ID) and the node is Ready again. Taint removed, node uncordoned...
   ```
3. Confirm independently:
   ```bash
   kubectl get node <your-target-node> -o jsonpath='{.spec.unschedulable}{"\n"}{.spec.taints}{"\n"}'
   ```
   Should now show `false` and no `patch-orchestrator/...` taint.

On a minikube/container-based node this step can't be genuinely tested — see
[TEST-REPORT.md](TEST-REPORT.md) §8 for why a simulated boot-ID patch doesn't work (kubelet
overwrites it back within about a minute).

---

## Cleanup

```bash
# Stop the app: Ctrl+C in Terminal A, or:
```
```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -match 'PatchOrchestrator' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```
```bash
kubectl delete namespace patch-test
kubectl uncordon <your-target-node>
kubectl patch node <your-target-node> --type=json -p='[{"op":"remove","path":"/spec/taints"}]'
rm patch-orchestrator/PatchOrchestrator/data/patch-schedule.json
rm patch-orchestrator/PatchOrchestrator/data/node-state.json
```
Revert `appsettings.Development.json` back to:
```json
{
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.Hosting.Lifetime": "Information" }
  }
}
```
