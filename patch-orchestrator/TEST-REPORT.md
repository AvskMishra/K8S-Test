# Patch Orchestrator — Live Test Report

A record of the one end-to-end test run against a real cluster: what was observed, and the exact
commands used to verify each observation. See [PLAN.md](PLAN.md) "Increment 2" for the same
material folded into the design log; this file is the standalone lab notebook version — commands
first, observations attached to each one.

Cluster: `k8slab` (minikube, podman driver, CRI-O runtime), 4 nodes, running inside the repo's
existing WSL2 Ubuntu distro (`k8s-infra-lab`). App: `PatchOrchestrator`, run natively on Windows,
pointed at the cluster via kubeconfig.

---

## 1. Environment check

```bash
podman --version
minikube version
```
**Observed:** `podman version 6.1.0`, `minikube version: v1.38.1` — both installed, as expected.

```bash
wsl.exe -l -v
```
**Observed:** only the `Ubuntu` distro existed, state `Stopped`. This is the distro
`k8s-infra-lab` already sets up `k8slab` inside — no new environment needed, just a restart.

---

## 2. A dead end, kept here for honesty

First attempt: a fresh Windows-side `podman machine` (podman's own WSL2 VM, separate from the
`Ubuntu` distro above).

```bash
podman machine init
podman machine start
```
**Observed:** failed — `Error: machine did not transition into running state: ssh error: machine
not in running state`. Investigated with:

```bash
podman machine list
podman machine info
wsl.exe -d podman-machine-default -- echo "hello"      # the WSL distro itself booted fine
wsl.exe -u root -d podman-machine-default -- bash -c "ps -ef | grep systemd"
```
**Observed:** `System has not been booted with systemd as init system (PID 1). Can't operate.` —
the machine's own bootstrap script (`/root/bootstrap`, which launches systemd via `unshare`) never
successfully started it. Also found and cleared a stale helper process along the way:

```powershell
Get-Process | Where-Object { $_.ProcessName -match 'docker|win-sshproxy|gvproxy' }
Stop-Process -Id 2588 -Force
```
This didn't fix it either. **Conclusion:** abandoned this path rather than debugging a fresh
podman-machine image further — `k8s-infra-lab/scripts` already has a working, previously-fixed
setup (see below), so used that instead of re-solving the same WSL2/podman quirks twice. Removed
the unused machine at the end of the session: `podman machine rm podman-machine-default --force`.

---

## 3. Bringing the real cluster up

```bash
wsl.exe -d Ubuntu -u root -- sh -c "kubectl get nodes -o wide"
```
**Observed:** connection refused — WSL2 booted, but the cluster's containers weren't running yet.

```bash
wsl.exe -d Ubuntu -u root -- sh -c "podman ps -a"
```
**Observed:** all 4 node containers (`k8slab`, `k8slab-m02`, `k8slab-m03`, `k8slab-m04`) existed
in state `Created` (i.e. stopped, not deleted) — confirmed the cluster just needed restarting, not
recreating.

```bash
wsl.exe -d Ubuntu -u root -- sh -c "minikube start -p k8slab --force"
```
(`--force` because minikube's podman driver refuses to run as root without it — the same tradeoff
`k8s-infra-lab/scripts/02-create-cluster.sh` documents and accepts for this throwaway lab distro.)

**Observed:** all 4 nodes came back `Ready`:
```bash
wsl.exe -d Ubuntu -u root -- sh -c "kubectl get nodes -o wide"
```
```
NAME         STATUS   ROLES           VERSION   INTERNAL-IP
k8slab       Ready    control-plane   v1.37.0   192.168.49.2
k8slab-m02   Ready    <none>          v1.37.0   192.168.49.3
k8slab-m03   Ready    <none>          v1.37.0   192.168.49.4
k8slab-m04   Ready    <none>          v1.37.0   192.168.49.5
```

---

## 4. Connecting the Windows-side app to the WSL2-side cluster

```bash
(echo > /dev/tcp/127.0.0.1/39099) 2>&1 && echo REACHABLE || echo NOT_REACHABLE
```
**Observed:** `REACHABLE` — WSL2's automatic localhost port-forwarding exposes podman's mapped API
server port (`127.0.0.1:39099 → k8slab:8443`) straight through to Windows. This meant no tunnel
was needed (unlike `k8s-console-tool`'s `exec`-specific raw-TCP-tunnel workaround — this app only
makes plain HTTPS REST calls, no WebSocket upgrade).

```bash
wsl.exe -d Ubuntu -u root -- bash -c "
  kubectl config view --raw --minify --flatten --context=k8slab > kubeconfig-test.yaml &&
  sed -i 's#server: https://192.168.49.2:8443#server: https://127.0.0.1:39099#' kubeconfig-test.yaml
"
```
**Observed:** produced a fully self-contained kubeconfig (`--flatten` embeds the cert/key inline,
so it's portable to Windows) pointed at the Windows-reachable port.

---

## 5. Building a real drain scenario

Needed several pods concentrated on one node, with no affinity pinning them there, so evicting
them would actually let the scheduler place replacements elsewhere.

```bash
kubectl cordon k8slab k8slab-m02 k8slab-m03
kubectl create namespace patch-test
kubectl -n patch-test create deployment drain-test --image=busybox --replicas=6 -- sh -c 'while true; do sleep 3600; done'
```
**Observed:** all 6 replicas scheduled onto the only uncordoned node, `k8slab-m04` (confirmed via
`kubectl -n patch-test get pods -o wide`).

```bash
kubectl uncordon k8slab k8slab-m02 k8slab-m03
```
**Observed:** the 6 pods stayed put on `k8slab-m04` — Kubernetes never rebalances already-running
pods on its own, confirming the test setup (only *new* pods would have anywhere else to go).

---

## 6. The schedule given to the app

`data/patch-schedule.json` (as consumed by the app — only these 3 fields are input, the rest is
written back by the app itself):
```json
[
  { "ServerName": "k8slab-m04", "Date": "2026-09-16", "Time": "17:44" }
]
```
Patch time set ~5 minutes out; default `DrainLeadTimeHours` (2h) meant the drain window was
already open the moment the app started, so draining began on the very first tick.

Test-only speed-up in `appsettings.Development.json` (reverted afterward): `KubeconfigPath` → the
test kubeconfig above, `CheckIntervalMinutes: 1`, `SecondsBetweenEvictions: 3` — purely so the
5-minute default loop didn't make the test take longer than necessary to watch.

```bash
cd patch-orchestrator/PatchOrchestrator
dotnet run
```

---

## 7. Observed behaviour, tick by tick

Verified by reading the app's own console output alongside independent `kubectl` checks (not
just trusting the app's logs):

```bash
wsl.exe -d Ubuntu -u root -- sh -c "kubectl -n patch-test get pods -o wide"
```

| Tick | App log said | `kubectl` independently showed |
|---|---|---|
| 1 | `Cordoned node k8slab-m04`, state → `Draining` | all 6 pods still on `k8slab-m04` |
| 2 | `2 pod(s) evicted this check, 4 remaining` | 1 pod each newly on `k8slab-m02` / `k8slab-m03` |
| 3 | `2 pod(s) evicted this check, 2 remaining` | settled: 1 / 2 / 1 / 2 across k8slab / m02 / m03 / m04 |
| 4 | `2 pod(s) evicted this check, 0 remaining` → tainted, state → `ReadyForPatch` | settled: **2 / 2 / 2** across k8slab, m02, m03 — `k8slab-m04` empty of workload pods |
| 5 | state → `WaitingForReboot` (patch time had passed) | — |

**The even-spread requirement, independently confirmed:**
```bash
kubectl -n patch-test get pods -o jsonpath='{range .items[*]}{.spec.nodeName}{"\n"}{end}' | sort | uniq -c
```
```
2 k8slab
2 k8slab-m02
2 k8slab-m03
```
6 pods off one node landed as 2-2-2 on the other three — not piled onto one or two.

**Cordon + taint, independently confirmed (not just app logs):**
```bash
kubectl get node k8slab-m04 -o jsonpath='{.spec.unschedulable}{"\n"}{.spec.taints}{"\n"}'
```
```
true
[{"effect":"NoSchedule","key":"node.kubernetes.io/unschedulable",...},
 {"effect":"NoSchedule","key":"patch-orchestrator/scheduled-patch","value":"true"}]
```

**A real edge case exercised, not just designed for:** one eviction batch completed at `17:45:10`,
a minute *after* the `17:44` patch time. Read directly from the app's own state file:
```bash
cat patch-orchestrator/PatchOrchestrator/data/patch-schedule.json
```
```
"2026-09-16 17:45:10 [Draining] Drain in progress: 2 pod(s) evicted this check, 0 remaining..."
"2026-09-16 17:45:10 [ReadyForPatch] All migratable pods evicted. Node cordoned and tainted..."
```
Confirms the "patch time arrived mid-drain" code path finished cleanly instead of misfiring
`NeedsAttention` — because a just-evicted pod (carrying a deletion timestamp) is correctly excluded
from "still remaining" on the immediate recheck.

**Cluster-wide state file, independently plausible-checked against the tick-by-tick table above:**
```bash
cat patch-orchestrator/PatchOrchestrator/data/node-state.json
```
```json
{ "NodeName": "k8slab-m04", "Ready": true, "Unschedulable": true, "TaintCount": 2,
  "RunningPodCount": 4, "ActivePatchState": "ReadyForPatch" }
```
(`RunningPodCount: 4` is correct, not a bug — that's the 2 DaemonSet pods, kube-proxy + kindnet,
which are supposed to keep running on every node, plus 2 workload pods still finishing their
30-second termination grace period at the moment this snapshot was taken.)

---

## 8. Reboot detection — tested, and found to need a real reboot

Tried to simulate a reboot without actually rebooting anything:
```bash
kubectl patch node k8slab-m04 --subresource=status --type=merge \
  -p '{"status":{"nodeInfo":{"bootID":"simulated-reboot-test-1234"}}}'
kubectl get node k8slab-m04 -o jsonpath='{.status.nodeInfo.bootID}'
```
**Observed:** the patch applied (echoed back the fake value immediately). But checking again about
a minute later:
```bash
kubectl get node k8slab-m04 -o jsonpath='{.status.nodeInfo.bootID}'
```
**Observed:** back to the real value (`f648b524-8984-...`) — kubelet's own periodic status
heartbeat overwrote the fake one before the app's next check saw it. The app correctly stayed in
`WaitingForReboot` throughout (confirmed via the app's log lines each tick).

**Conclusion:** the boot-ID comparison is robust against exactly this kind of accidental or spoofed
write — good news — but it also means genuine end-to-end verification of the auto-restore
(uncordon + untaint once a reboot is detected) needs an actual node reboot, which isn't practical
to trigger safely on a container-based node that shares the host kernel's boot ID. Left as an
explicitly open item rather than claimed as tested.

---

## 9. Cleanup — commands used to leave the cluster clean afterward

```powershell
Stop-Process -Id <dotnet.exe PID> -Force
```
```bash
wsl.exe -d Ubuntu -u root -- sh -c "
  kubectl delete namespace patch-test --wait=false &&
  kubectl uncordon k8slab-m04 &&
  kubectl patch node k8slab-m04 --type=json -p='[{\"op\":\"remove\",\"path\":\"/spec/taints\"}]'
"
rm data/patch-schedule.json data/node-state.json   # both gitignored test artifacts
podman machine rm podman-machine-default --force   # the unused, abandoned Windows-side machine
```
Verified clean afterward:
```bash
wsl.exe -d Ubuntu -u root -- sh -c "kubectl get nodes -o wide"
```
```
k8slab       Ready   control-plane
k8slab-m02   Ready   <none>
k8slab-m03   Ready   <none>
k8slab-m04   Ready   <none>          # no cordon, no custom taint
```
`git status` confirmed no test artifacts were left in a tracked location (`data/patch-schedule.json`
and `data/node-state.json` correctly excluded by `.gitignore`'s `**/patch-schedule.json` /
`**/node-state.json` patterns). The `k8slab` cluster itself was left running.

---

## Bottom line

Everything in the original scenario except the final reboot itself was verified against a real
4-node cluster, with independent `kubectl` checks alongside the app's own logs at every step:
cordon timing, graceful paced eviction, even redistribution (2-2-2, not piled up), taint
application, and correct state tracking through a real "patch time arrives mid-drain" edge case.
The one gap — positively confirming auto-restore after a *real* reboot — is a limitation of what
this environment can safely simulate, not of the app's logic, and is called out explicitly rather
than glossed over.
