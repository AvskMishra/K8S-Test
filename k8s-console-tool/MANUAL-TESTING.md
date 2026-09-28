# K8sExplorer — manual testing guide

Step-by-step instructions to try every K8sExplorer feature yourself against the local `k8slab` cluster, from **Windows PowerShell**. Each change comes with a step to confirm it happened and a step to undo it, so the cluster ends up where it started.

Two ways to run the tool, both covered below:

- **Command line** — `K8sExplorer <resource> <verb> <names...>`; runs once, no prompts (Parts 2–5)
- **Interactive menus** — `K8sExplorer` with no arguments; pick from lists (Part 6)

---

## Part 0 — Start the cluster

The cluster runs **inside the WSL Ubuntu distro**, as root, using Podman there. It does *not* use the Windows `podman machine`.

```powershell
# Start (or restart) the 4-node cluster — takes ~1-2 minutes
wsl -d Ubuntu -u root -- minikube start -p k8slab --force

# Every node should be Ready
wsl -d Ubuntu -u root -- kubectl get nodes

# Every pod should be Running
wsl -d Ubuntu -u root -- kubectl get pods -A
```

Expected:

```
NAME         STATUS   ROLES           AGE   VERSION
k8slab       Ready    control-plane   20d   v1.37.0
k8slab-m02   Ready    <none>          20d   v1.37.0
k8slab-m03   Ready    <none>          20d   v1.37.0
k8slab-m04   Ready    <none>          13d   v1.37.0
```

If any `product-*` pod shows `ErrImageNeverPull`, that node is missing the locally built images. Load them into every node:

```powershell
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/K8S-Test/app/scripts/load-images-to-cluster.sh
```

If nodes go `NotReady` or WSL commands start hanging, see [Troubleshooting](#troubleshooting).

---

## Part 1 — Connect the app to the cluster (one-time setup per session)

### 1.1 Write a kubeconfig the Windows app can use

minikube already publishes the API server on `127.0.0.1:<port>` inside WSL, and WSL forwards that to Windows. So no tunnel is needed:

```powershell
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/K8S-Test/app/scripts/write-local-kubeconfig.sh
```

Expected: `wrote .../k8s-console-tool/kubeconfig-local.yaml (server https://127.0.0.1:39099)`.

Re-run this if you ever **delete and recreate** the cluster, because the port can change. A normal restart keeps the same port. The file holds cluster credentials and is git-ignored.

### 1.2 Build the app

```powershell
cd C:\CodeBase\K8S-Test\k8s-console-tool\K8sExplorer
dotnet build
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

### 1.3 Create a short command for this PowerShell window

Paste this into the same PowerShell window you'll test in. It only lasts for that window.

```powershell
function k8x { & "C:\CodeBase\K8S-Test\k8s-console-tool\K8sExplorer\bin\Debug\net10.0\K8sExplorer.exe" --kubeconfig "C:\CodeBase\K8S-Test\k8s-console-tool\kubeconfig-local.yaml" @args }

# A helper to check results independently with kubectl inside WSL
function kc { wsl -d Ubuntu -u root -- kubectl @args }
```

Check it works:

```powershell
k8x --help
k8x namespaces list
```

> Without the helper, the equivalent is `dotnet run -- --kubeconfig ..\kubeconfig-local.yaml nodes list` from the `K8sExplorer` folder.

### 1.4 Pick some pod names to test with

Pod names change whenever pods are recreated. Re-run this block whenever a later step says so.

```powershell
$api = wsl -d Ubuntu -u root -- bash -c "kubectl get pods -n product-catalog -l app=product-api -o jsonpath='{.items[0].metadata.name}'"
$api2 = wsl -d Ubuntu -u root -- bash -c "kubectl get pods -n product-catalog -l app=product-api -o jsonpath='{.items[1].metadata.name}'"
$web = wsl -d Ubuntu -u root -- bash -c "kubectl get pods -n product-catalog -l app=product-frontend -o jsonpath='{.items[0].metadata.name}'"
$web2 = wsl -d Ubuntu -u root -- bash -c "kubectl get pods -n product-catalog -l app=product-frontend -o jsonpath='{.items[1].metadata.name}'"
"$api  $api2  $web  $web2"
```

---

## Part 2 — Read-only commands (safe, change nothing)

| # | Command | What you should see |
|---|---|---|
| 2.1 | `k8x nodes list` | 4 nodes, Status `Ready`, a Taints column |
| 2.2 | `k8x nodes get k8slab-m02` | Details: OS, kubelet, addresses, capacity/allocatable, conditions, taints, labels |
| 2.3 | `k8x nodes get k8slab-m02 k8slab-m03` | Details for both nodes, one after the other |
| 2.4 | `k8x nodes events k8slab-m02` | Events for that node (or "No events found") |
| 2.5 | `k8x nodes pods k8slab-m02` | Every pod on that node, all namespaces, with a Namespace column |
| 2.6 | `k8x pods list -n product-catalog` | mongo + 3 product-api + 3 product-frontend, all `Running` |
| 2.7 | `k8x pods list -A` | Pods from every namespace, with a Namespace column |
| 2.8 | `k8x pods list -A --node k8slab-m03` | Only the pods on m03 |
| 2.9 | `k8x pods get $api -n product-catalog` | Pod status, node, IP, containers, labels, recent events |
| 2.10 | `k8x pods logs $api -n product-catalog --tail 5` | Last 5 log lines of the API |
| 2.11 | `k8x pods exec $api -n product-catalog printenv MongoDbSettings__DatabaseName` | `ProductCatalog` |
| 2.12 | `k8x pods exec $api -n product-catalog ls -la /app/appsettings.json` | One `ls` line (dash arguments work) |
| 2.13 | `k8x pods exec $api -n product-catalog "ls /app \| wc -l"` | A number. One quoted string runs through `sh -c`, so pipes work |
| 2.14 | `k8x deployments list -n product-catalog` | mongo, product-api, product-frontend with ready counts |
| 2.15 | `k8x services list -n product-catalog` | Services with type, cluster IP, ports |
| 2.16 | `k8x namespaces list` | 5 namespaces |
| 2.17 | `k8x events -n product-catalog` | Recent events, newest first |

> In a normal terminal the tables use the full window width. They only wrap narrowly when output is redirected.

---

## Part 3 — Node operations (change the cluster, each step undone)

Use the worker nodes (`k8slab-m02`/`m03`/`m04`) for these tests, not the control-plane node `k8slab`.

### 3.1 Cordon / uncordon

```powershell
k8x nodes cordon k8slab-m03
```
Expected: `ok     cordon k8slab-m03`.

**Check:** `k8x nodes list` shows m03 as `Ready,SchedulingDisabled`, with the taint `node.kubernetes.io/unschedulable:NoSchedule`, which Kubernetes adds automatically.

**Several nodes at once:**
```powershell
k8x nodes cordon k8slab-m02 k8slab-m03
```

**Undo:**
```powershell
k8x nodes uncordon k8slab-m02 k8slab-m03
```
**Check:** `k8x nodes list` shows both as plain `Ready`, with no taints.

### 3.2 Taints

**Add** (format `key[=value]:Effect`, where Effect is `NoSchedule`, `PreferNoSchedule` or `NoExecute`):
```powershell
k8x nodes taint k8slab-m03 dedicated=db:NoSchedule maint:PreferNoSchedule
```
Expected: two `ok` lines.

**Check:**
```powershell
k8x nodes get k8slab-m03        # "Taints: dedicated=db:NoSchedule, maint:PreferNoSchedule"
kc get node k8slab-m03 -o jsonpath='{.spec.taints}'
```

**Update:** re-applying the same key and effect with a new value replaces the old one:
```powershell
k8x nodes taint k8slab-m03 dedicated=cache:NoSchedule
```

**Remove** (trailing `-`; `key:Effect-` removes one effect, `key-` removes every effect for that key):
```powershell
k8x nodes taint k8slab-m03 dedicated:NoSchedule- maint-
```
**Check:** `k8x nodes get k8slab-m03` shows `Taints: <none>`.

**Error case:** removing a taint that isn't there:
```powershell
k8x nodes taint k8slab-m03 notthere:NoSchedule-; "exit=$LASTEXITCODE"
```
Expected: `failed ... taint not found on this node`, `exit=1`.

> ⚠️ `NoExecute` evicts every running pod on that node that doesn't tolerate the taint. Only use it on purpose, and remove it straight away.

### 3.3 Labels

```powershell
k8x nodes label k8slab-m02 k8slab-m03 disk=ssd tier=test
```
**Check:** `kc get nodes -L disk -L tier` shows `ssd` and `test` on those two nodes.

**Remove:**
```powershell
k8x nodes label k8slab-m02 k8slab-m03 disk- tier-
```
**Check:** `kc get nodes -L disk -L tier` shows those columns empty.

### 3.4 Drain

Drain cordons the node, then evicts its pods. Eviction honours PodDisruptionBudgets, and drain waits until the pods have actually stopped. Their Deployments recreate them on other nodes.

**a) Safety check: drain refuses without flags.** Every node runs DaemonSet pods (kindnet and kube-proxy):
```powershell
k8x nodes drain k8slab-m03; "exit=$LASTEXITCODE"
```
Expected: `blocked kube-system/kindnet-... is DaemonSet-managed`, then `drain aborted before evicting anything; node left cordoned`, `exit=1`. **Nothing was evicted**, but the node *is* cordoned, the same way kubectl behaves.
```powershell
k8x nodes uncordon k8slab-m03
```

**b) A real drain.** First find a worker that has app pods on it:
```powershell
k8x pods list -n product-catalog      # look at the Node column; pick a worker, e.g. k8slab-m02
k8x nodes drain k8slab-m02 --ignore-daemonsets --delete-emptydir-data --timeout 150; "exit=$LASTEXITCODE"
```
Expected:
```
  cordoned k8slab-m02
  evicting product-catalog/product-api-...
  evicting product-catalog/product-frontend-...
  k8slab-m02 drained
  skipped kube-system/kindnet-... (DaemonSet-managed)
  skipped kube-system/kube-proxy-... (DaemonSet-managed)
k8slab-m02 drained (N pod(s) evicted). Uncordon it to bring it back.
exit=0
```

**Check:**
```powershell
k8x nodes pods k8slab-m02                  # only kindnet + kube-proxy remain
k8x pods list -n product-catalog           # the evicted pods have been recreated on other nodes and are Running
k8x nodes list                             # m02 is Ready,SchedulingDisabled
```

**Undo:**
```powershell
k8x nodes uncordon k8slab-m02
```

Drain options:

| Flag | Meaning |
|---|---|
| `--ignore-daemonsets` | Skip DaemonSet pods instead of refusing. Needed on practically every node |
| `--delete-emptydir-data` | Allow evicting pods that use emptyDir volumes. That data is lost |
| `--force` | Also delete pods with no controller. They will **not** come back |
| `--timeout 120` | Seconds to wait (default 120) |

> Don't drain every node at once: evicted pods would have nowhere to go. `mongo` has a single replica, so draining its node briefly takes the database down while it moves. That's expected.

---

## Part 4 — Pod operations

Namespace: `-n <ns>` (default `default`). Re-run the name-picking block from [1.4](#14-pick-some-pod-names-to-test-with) after any step that recreates pods.

### 4.1 Labels
```powershell
k8x pods label $api -n product-catalog smoke=yes
k8x pods get $api -n product-catalog          # Labels: ..., smoke=yes
k8x pods label $api -n product-catalog smoke-
k8x pods get $api -n product-catalog          # smoke is gone
```
> ⚠️ Don't change the `app` label: the ReplicaSet would lose track of the pod and create a replacement.

### 4.2 Delete (restart one pod)
```powershell
k8x pods delete $web -n product-catalog
k8x pods list -n product-catalog              # $web is Terminating or gone; a new product-frontend pod is starting
```
A pod owned by a Deployment is recreated automatically, so deleting it effectively restarts it.

**Force delete** (grace period 0, for pods stuck in `Terminating`):
```powershell
k8x pods delete $web2 -n product-catalog --force
```

### 4.3 Evict
```powershell
k8x pods evict $api2 -n product-catalog
```
Works like delete, but the API server refuses if it would break a PodDisruptionBudget. There are no PDBs in this app, so it succeeds.

### 4.4 Rollout restart (restart the whole Deployment)
Refresh names first (block 1.4), then:
```powershell
k8x pods restart $api $api2 -n product-catalog
```
Expected: **one** line, `ok     rollout restart Deployment product-catalog/product-api`. Both pods belong to the same Deployment, so it's restarted only once.

**Check:**
```powershell
kc rollout status deploy/product-api -n product-catalog      # waits until "successfully rolled out"
k8x pods list -n product-catalog                             # 3 new product-api pods with a new hash in the name, Age a few seconds
```

### 4.5 Error cases
```powershell
k8x pods delete does-not-exist -n product-catalog; "exit=$LASTEXITCODE"   # failed ... not found, exit=1
k8x pods restart does-not-exist -n product-catalog; "exit=$LASTEXITCODE"  # failed ... not found, exit=1
```

---

## Part 5 — Argument checks and exit codes

Mistakes in the arguments are caught **before** the tool contacts the cluster, and exit with code 2.

```powershell
k8x nodes taint k8slab-m02 bad; "exit=$LASTEXITCODE"                    # needs a taint spec -> 2
k8x nodes taint k8slab-m02 key=v:Bogus; "exit=$LASTEXITCODE"            # invalid effect -> 2
k8x nodes taint k8slab-m02 dedicated=db; "exit=$LASTEXITCODE"           # missing :Effect -> 2
k8x nodes label k8slab-m02 bad; "exit=$LASTEXITCODE"                    # needs key=value or key- -> 2
k8x nodes drain k8slab-m02 --timeout abc; "exit=$LASTEXITCODE"          # not a number -> 2
k8x pods exec $api -n product-catalog; "exit=$LASTEXITCODE"             # no command -> 2
k8x nodes frobnicate; "exit=$LASTEXITCODE"                              # unknown verb -> 2
k8x pods list --bogus; "exit=$LASTEXITCODE"                             # unknown option -> 2
k8x nodes get does-not-exist; "exit=$LASTEXITCODE"                      # not found -> 1
```

| Exit code | Meaning |
|---|---|
| 0 | Everything succeeded |
| 1 | An operation failed on at least one of the named items (the others still ran) |
| 2 | Bad arguments; nothing was sent to the cluster |

---

## Part 6 — Interactive menus

```powershell
cd C:\CodeBase\K8S-Test\k8s-console-tool\K8sExplorer
dotnet run
```

At the kubeconfig prompt, type:
```
C:\CodeBase\K8S-Test\k8s-console-tool\kubeconfig-local.yaml
```
Just pressing Enter uses `kubeconfig-direct.yaml`, which only works while the old `port-forward` tunnel is running.

**Keys:** ↑/↓ to move, **Enter** to choose. In the multi-select lists (the node and pod operations): **Space** toggles an item, **Enter** accepts. Every change asks for a y/n confirmation first.

| Menu | Try this | Then check with |
|---|---|---|
| 1 → 1.1 | List nodes | — |
| 1 → 1.4 Cordon | Space on `k8slab-m03`, Enter, `y` | 1.1 shows `Ready,SchedulingDisabled` |
| 1 → 1.5 Uncordon | Select m03, `y` | 1.1 shows `Ready` |
| 1 → 1.7 Add taint | m03; key `dedicated`, value `db`, effect `NoSchedule` | 1.2 on m03 shows the taint |
| 1 → 1.8 Remove taint | m03; pick `dedicated:NoSchedule` | 1.2 shows `Taints: <none>` |
| 1 → 1.9 / 1.10 | Add label `disk=ssd` to m02, then remove it | 1.2 → Labels table |
| 1 → 1.6 Drain | Pick a worker with app pods; answer Yes to "ignore DaemonSets" and to emptyDir, No to force, timeout 120, confirm `y` | Pods move away; then **1.5 Uncordon** it |
| 2 → 2.5 Delete | Namespace `product-catalog`, Space on one frontend pod, choose *Graceful*, `y` | 2.1 shows a replacement pod |
| 2 → 2.6 Evict | Select a pod, `y` | 2.1 |
| 2 → 2.7 Rollout restart | Select 2 product-api pods | One restart line for the Deployment |
| 2 → 2.8 Pod label | Add `smoke=yes`, then remove it | 2.2 shows the Labels line |
| 2 → 2.3 / 2.4 | Logs and exec (e.g. `printenv HOSTNAME`) | — |
| 0 | Exit | — |

To cancel a multi-select without doing anything, press **Enter** without selecting anything ("No nodes selected"), or answer **n** at the confirmation.

---

## Part 7 — Optional: automated check of all write operations

A single command runs every write operation against the last worker node (`k8slab-m04`). It adds and removes a label, adds and removes a taint, cordons, drains, then uncordons. It also rollout-restarts `product-api` and adds and removes a pod label:

```powershell
cd C:\CodeBase\K8S-Test\k8s-console-tool\K8sExplorer
dotnet run -- --ops-smoke-test "C:\CodeBase\K8S-Test\k8s-console-tool\kubeconfig-local.yaml"
```
Expected last line: `Ops smoke test completed successfully.`

The read-only equivalent is `dotnet run -- --smoke-test "...kubeconfig-local.yaml"`.

---

## Part 8 — Final cleanup checklist

After testing, confirm the cluster is back to normal:

```powershell
k8x nodes list                                   # all 4 plain "Ready", Taints <none>
kc get nodes -L disk -L tier                        # disk/tier columns empty
k8x pods list -n product-catalog                 # 7 pods, all Running
```

If a node still says `SchedulingDisabled`: `k8x nodes uncordon <node>`.
If a test taint is left over: `k8x nodes taint <node> <key>-`.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `No connection could be made` / connection refused | The cluster isn't running, or the port changed. Run Part 0, then 1.1 again |
| `kubeconfig file not found` | Run step 1.1 |
| `ErrImageNeverPull` on some pods | Run `load-images-to-cluster.sh` (Part 0) |
| A node is `NotReady`; WSL commands like `podman ps` hang; load is very high | A node container has gone bad (it happened with `k8slab-m04`: its kubelet was stuck at 200% CPU and the container ignored kill). Restart WSL and the cluster: `wsl --shutdown`, then `wsl -d Ubuntu -u root -- minikube start -p k8slab --force`. **This stops everything running in WSL.** Check load with `wsl -d Ubuntu -u root -- cat /proc/loadavg`; on 8 CPUs, under ~8 is healthy |
| Drain says `evicted but still terminating at timeout` | The node's kubelet isn't responding (node NotReady), so nobody confirms the pods have stopped. Fix the node, or force-delete the stuck pods with `k8x pods delete <pod> -n <ns> --force` |
| Drain says `blocked ... PodDisruptionBudget` and retries | A PDB forbids the eviction right now. Drain retries every 5 s until `--timeout` |
| `exec` output looks wrong with quotes | Options such as `-n` and `-c` must come *before* the command. Separate words run as-is (like kubectl); for shell features, pass one quoted string: `"ls /app \| wc -l"` |
| Pod is stuck `Terminating` forever | `k8x pods delete <pod> -n <ns> --force` |
