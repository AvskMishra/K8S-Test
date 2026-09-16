# Patch Orchestrator — Plan, Reasoning, and Implementation Log

A .NET background service that keeps a server (a Kubernetes node) safe to patch. Given a
`serverName` + `date` + `time`, it automatically: stops new pods landing on that node ahead of
time, gracefully migrates the pods already there onto other nodes (spread evenly, not piled onto
one or two), marks the node as ready for the 3rd party's patch + reboot, and — once it detects the
reboot actually happened — puts the node back into normal service. The 3rd party's own patch and
reboot step is explicitly **out of scope**: this app only ever prepares the node for that and
cleans up afterwards.

Builds directly on the same approach already proven in this repo (`k8s-console-tool`,
`k8s-dashboard`): talk to the Kubernetes API server directly via the official `KubernetesClient`
C# library, using a kubeconfig — never SSH into a node, never shell out to `kubectl`.

---

## 1. Why a new, standalone project

Considered adding this as a `BackgroundService` inside `k8s-dashboard`'s existing API instead.
Decided against it: this app's failure mode is completely different from a browsing tool's — if
it crashes or is misconfigured, a node could silently miss its drain window. Keeping it a separate
deployable means its lifecycle, logs, and restart behaviour are independent of the dashboard, and
it can run on its own schedule (a Windows Service / systemd unit / a small always-on pod) without
dragging a web UI along with it.

## 2. The state machine (`Models/PatchScheduleState.cs`, `Services/PatchWorkflowEngine.cs`)

Every schedule moves through one direction, `Pending → Draining → ReadyForPatch →
WaitingForReboot → Completed`, with `NeedsAttention` as an escape hatch reachable from several
points whenever something doesn't go as planned (unknown node name, pods stuck behind a
PodDisruptionBudget, no reboot ever observed). The engine is **not** a long-running loop that
blocks for two hours — it's called once per schedule, every tick of the 5-minute timer, and each
call does only the one next thing that's due *right now*. That's what makes it safe to restart the
whole app at any point: on the next tick it just re-reads `data/patch-schedule.json`, sees what
state each schedule is in, and carries on.

| State | What triggered entering it | What the app does while in it |
|---|---|---|
| `Pending` | Schedule just added, more than 2h (configurable) from patch time | Nothing yet |
| `Draining` | Now within the lead-time window | Node cordoned; evicts a few pods per tick |
| `ReadyForPatch` | No more migratable pods left | Node cordoned **and** tainted; waiting for patch time |
| `WaitingForReboot` | Patch time reached | Watching for the node's boot ID to change |
| `Completed` | Reboot detected + node Ready again | Taint removed, uncordoned — done |
| `NeedsAttention` | Anything unexpected | App stops acting automatically; human reads `History` |

## 3. Cordon *and* drain *and* taint — why all three, and in that order

- **Cordon** (`spec.unschedulable = true`) happens first, right when the lead-time window opens.
  This is the one flag that stops *new* pods landing on the node — cheap, instant, reversible, and
  it's what lets the drain proceed without the scheduler racing to refill the node as pods leave.
- **Draining** (graceful pod eviction) happens next, spread across the whole window — see §4.
- **Taint** is added only once draining has actually finished (or the window has run out). It's a
  second, independent signal ("this node is scheduled for disruption") layered on top of cordon —
  redundant with cordon for blocking new pods, but it's the more discoverable, standard way for
  anyone else looking at the cluster (`kubectl describe node`) to see *why* a node is cordoned,
  and it's what the user's original ask specifically called out doing "just before patch time".

`NoSchedule`, not `NoExecute`, was chosen for the taint: `NoExecute` would also evict DaemonSet
pods (`kube-proxy`, the CNI plugin, log shippers, ...), which are supposed to keep running on a
node right up until it actually reboots — they're not part of "migrate the workload elsewhere",
they're infrastructure that belongs on every node including this one, until the last second.

## 4. How "spread evenly, not 5-5 on 2 nodes" is actually achieved

The literal ask: 10 pods coming off one node should land as 2-2-2-2-2 across five other nodes, not
5-5 across two. Two designs were considered:

- **Add `topologySpreadConstraints` to every workload, then bulk-evict.** This is the
  Kubernetes-native way to *guarantee* an even spread, but it means this app would have to reach
  into and modify Deployments/StatefulSets it doesn't own — a much bigger blast radius (a bad
  constraint can make a Deployment un-schedulable entirely), and a very different kind of
  permission than "drain a node".
- **Evict a few pods at a time, with a pause, and let the default scheduler's own bin-packing do
  the spreading (chosen).** The default Kubernetes scheduler scores candidate nodes and favours
  the *least loaded* one for each new pod. If pods are evicted one at a time (or in small batches,
  `MaxEvictionsPerTick`, default 3) with a short pause afterwards
  (`SecondsBetweenEvictions`, default 15s) for the replacement to actually land, each successive
  pod naturally lands on whatever node is currently least loaded — which drifts across all
  available nodes rather than piling onto whichever single node happened to answer first. Draining
  is also paced across the *entire* 2-hour lead-time window (a few pods per 5-minute tick), not
  done in one burst, for the same reason plus general cluster-stability courtesy.

This was the explicit trade-off accepted with the answer to the clarifying question asked before
building this: simpler, lower blast-radius, "good enough" spreading rather than a
mathematically-guaranteed one. If the cluster later needs a hard guarantee, adding
`topologySpreadConstraints` to specific workloads remains a natural, separate follow-up — it isn't
blocked by anything built here.

## 5. What "gracefully migrate, not suddenly restart" means in code

`NodePatchOperations.TryEvictPodAsync` calls the Kubernetes **Eviction API**
(`pods/eviction` — the same subresource `kubectl drain` itself uses), not a plain pod delete. Two
things fall out of using eviction specifically, both required by the ask and neither hand-rolled:
- Kubernetes lets the pod's own `terminationGracePeriodSeconds` run before killing it — the normal
  clean-shutdown path every pod already has, unchanged.
- If the pod's owner has a `PodDisruptionBudget` that eviction would violate (e.g. "always keep at
  least 2 replicas"), the API server itself **refuses** the eviction (HTTP 429). The app treats
  this as "try again next tick", never as a reason to force it through — Kubernetes' own
  disruption-budget guarantee for that workload is respected, not overridden.

## 6. Detecting the reboot actually happened

The one genuinely tricky bit: a node's `Ready` condition being `True` doesn't tell you whether it
just rebooted or was Ready the whole time (e.g. if the 3rd party's patch was simply delayed).
Kubernetes exposes `status.nodeInfo.bootID` on every node — a value that is guaranteed to change
across a real reboot. This app captures that value the moment draining finishes
(`BootIdAtDrainComplete`, stored right in `patch-schedule.json`), and after patch time, compares
the *live* boot ID against it on every tick. A changed boot ID + `Ready == True` is the trigger to
uncordon and untaint automatically. If no reboot is observed within `MaxWaitForRebootHours`
(default 6h) past the scheduled time, the app gives up guessing and moves the schedule to
`NeedsAttention` instead of silently waiting forever or guessing wrong.

## 7. Knowing the state of every server, not just ones being patched

`Services/NodeStateReporter.cs` runs every tick, for every node in the cluster, regardless of
whether a patch is scheduled for it — this is what answers "what's the current state of all the
servers in this cluster" from the original ask. It merges two things into
`data/node-state.json`:
- live facts read straight from the Kubernetes API (`Ready`, cordoned?, taint count, running pod
  count), and
- whichever `PatchScheduleState` (if any) is currently active for that node.

This is a plain JSON file, not a database or an API, matching the "very simple" ask and the same
file-backed-store pattern `k8s-dashboard` already uses for its cluster registry. It's the natural
seed for a future "show me all node states" view in `k8s-dashboard` — but that UI wasn't asked
for yet, so it wasn't built.

## 8. Input format and persistence

Schedules live in `data/patch-schedule.json` (gitignored — see repo `.gitignore`, which already
had `patch-orchestrator/data/patch-schedule.json` and `patch-orchestrator/data/node-state.json`
listed before this app was written, confirming this exact shape/location was already the intended
design). `data/patch-schedule.sample.json` is committed and shows the shape an external system (or
a person) needs to write: just `ServerName`, `Date`, `Time` — every other field
(`State`, `BootIdAtDrainComplete`, `History`) is written by this app itself and should be left out
when *adding* a new schedule.

A JSON file, polled every 5 minutes, was chosen over this app exposing its own API or polling
someone else's — no extra infrastructure, and it matches how `k8s-dashboard` already persists its
own state (`data/clusters.json`). Nothing stops a future increment from adding a thin
`POST /api/schedules` endpoint that writes into the same file if a push model becomes useful later.

## 9. What's explicitly out of scope

- **The patch itself and the reboot** — performed by the 3rd party, entirely outside this app.
  This app only prepares the node beforehand and restores it afterwards.
- **A guaranteed mathematical spread** — see §4; the chosen approach is "spreads out well in
  practice", not "provably exact".
- **Authentication on this app** — it's a background service with no exposed endpoints in this
  increment, so there's nothing to authenticate against yet.
- **Multi-cluster support** — one kubeconfig, one cluster, matching the ask's "a cluster with 3
  hybrid nodes" scope. `k8s-dashboard`'s multi-cluster registry pattern would be the template to
  follow if this ever needs to watch more than one cluster.
- **Time zones** — `Date`/`Time` are parsed as the local time of the machine running this app. A
  deployment spanning multiple time zones would want to switch this to explicit UTC timestamps.

---

## Progress log

### Increment 1 — scaffold, state machine, RBAC (this one)
- Scaffolded `PatchOrchestrator` (.NET 10 Worker Service) with `KubernetesClient` 19.0.2, matching
  the exact package version already used by `k8s-console-tool` / `k8s-dashboard`.
- Confirmed the exact Kubernetes API calls needed by reflecting the installed `KubernetesClient`
  assembly directly (not guessed from memory): `CoreV1.PatchNodeAsync` (merge patch on
  `spec.unschedulable` / `spec.taints`), `CoreV1.CreateNamespacedPodEvictionAsync` with a
  `policy/v1` `V1Eviction`, `CoreV1.ListPodForAllNamespacesAsync` with a `spec.nodeName=...` field
  selector, and `status.nodeInfo.bootID` for reboot detection.
- Built the full state machine, the node/pod Kubernetes operations, the JSON-file schedule store,
  the node-state snapshot reporter, and the 5-minute worker loop.
- `dotnet build` succeeds clean (0 warnings, 0 errors).
- Fixed a real bug found while checking the repo's `.gitignore`: the entries anticipating this
  project's data files assumed `patch-orchestrator/data/...`, but following this repo's own
  convention of nesting the `.csproj` under a project-name folder, the actual path is
  `patch-orchestrator/PatchOrchestrator/data/...`. Widened the patterns to `**/patch-schedule.json`
  / `**/node-state.json` (matching the existing `**/data/clusters.json` style already in the file)
  and confirmed with `git check-ignore` that the real files are now excluded while the committed
  `.sample.json` is not.
- Hardened `Worker.cs` so one schedule's exception can't stop unrelated schedules from being
  processed/saved in the same tick (each schedule now gets its own try/catch).

### Increment 2 — live verification against the real `k8slab` cluster
WSL2 was stopped when Increment 1 was written; this increment brought the lab cluster back up and
ran the real scenario end-to-end, using podman + minikube exactly as the user asked, following the
already-proven setup in `k8s-infra-lab/scripts` (a Windows-side `podman machine` was tried first
and abandoned — its bundled systemd never came up cleanly; `k8s-infra-lab`'s approach of running
podman *inside* the existing WSL2 Ubuntu distro, which already has the WSL2-specific fixes from
`00-fix-wsl-networking.sh` applied, is what actually worked).

**Setup**: restarted `minikube start -p k8slab --force` inside the Ubuntu WSL2 distro (now 4 nodes:
`k8slab` control-plane + `k8slab-m02/m03/m04` workers — a node was added since the cluster was
first created). Generated a portable kubeconfig (`kubectl config view --raw --minify --flatten`)
pointed at `https://127.0.0.1:<podman-forwarded-port>` — confirmed WSL2's localhost port forwarding
exposes it directly to Windows, so PatchOrchestrator (running natively on Windows) could reach it
with no tunnel needed (the raw-TCP-tunnel workaround `k8s-console-tool` needed was specifically for
`exec`'s WebSocket upgrade; this app only makes plain HTTPS REST calls). Concentrated 6 test pods
(`busybox`, namespace `patch-test`) onto `k8slab-m04` by cordoning the other three nodes during
scheduling, then uncordoning them — so the replacements evicted pods would create had somewhere
else to land. Used `appsettings.Development.json` to point at the test kubeconfig and speed up the
loop (`CheckIntervalMinutes: 1`, `SecondsBetweenEvictions: 3`) purely for faster observation; the
committed defaults are untouched.

**Result — the full scenario worked exactly as designed, against a real cluster:**
1. First tick: `k8slab-m04` cordoned immediately, state → `Draining`.
2. Pods evicted 2 at a time (`MaxEvictionsPerTick`), each batch's replacements landing on a
   *different* node than the last — final distribution settled at **2 / 2 / 2** across `k8slab`,
   `k8slab-m02`, `k8slab-m03`. This is the "2-2 on 5 nodes, not 5-5 on 2" requirement, confirmed
   with a real ReplicaSet and a real scheduler, not just reasoned about.
3. One batch's eviction happened *after* the schedule's patch time had technically passed
   (`17:45` vs a `17:44` patch time) — this exercised the "time's up" branch in
   `ContinueDrainingAsync` for real: it correctly recognized the just-evicted pods as no longer
   "migratable" (they already carry a deletion timestamp) and proceeded straight to
   `FinishDrainingAsync` rather than misfiring `NeedsAttention`.
4. `FinishDrainingAsync` tainted the node (`patch-orchestrator/scheduled-patch=true:NoSchedule`,
   alongside Kubernetes' own automatic `node.kubernetes.io/unschedulable` taint from the cordon)
   and captured its real boot ID. State → `ReadyForPatch`, then → `WaitingForReboot` on the next
   tick once patch time had passed — all confirmed directly against `kubectl get node -o
   jsonpath=...` and the node's own status, not just the app's logs.
5. `data/patch-schedule.json`'s `History` and `data/node-state.json` both read exactly as designed
   — see the file shapes in README.md/ARCHITECTURE.md, now confirmed accurate against real output
   rather than just planned.
6. **Reboot-detection, honestly tested and found to need a real reboot**: tried simulating one by
   patching the node's `status.nodeInfo.bootID` directly
   (`kubectl patch node ... --subresource=status`). Kubelet's own periodic status heartbeat
   overwrote it back to the real value within roughly a minute — before the app's next check saw
   the fake one — so the app correctly stayed in `WaitingForReboot` rather than being fooled. This
   confirms the mechanism is robust against exactly this kind of accidental/spoofed write, but it
   also means this specific step could not be *positively* verified end-to-end in this session
   (that needs an actual node reboot, which isn't practical to trigger on a container-based node
   sharing the host kernel's boot ID). Documented here rather than silently left untested.

**Cleanup performed after testing**: stopped the app, deleted the `patch-test` namespace,
uncordoned and removed all taints from `k8slab-m04`, reverted `appsettings.Development.json` to
its committed (empty-override) state, deleted the generated `data/patch-schedule.json` and
`data/node-state.json` test artifacts (both gitignored, regenerated on next real use), and removed
the unused Windows-side `podman machine` created during the abandoned first attempt. The `k8slab`
cluster itself (inside WSL2 Ubuntu) was left running.
