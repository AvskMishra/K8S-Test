# Example 6 — Deployment vs StatefulSet: Stateless & Stateful Apps

## Files

| File | What it shows |
|------|---------------|
| `01-dotnet-deployment.yaml` | Stateless .NET app — 3 identical replicas behind ClusterIP |
| `02-mysql-secret.yaml` | MySQL root password + app connection string |
| `03-mysql-configmap.yaml` | Init script that detects master vs replica at pod startup |
| `04-mysql-headless-service.yaml` | Headless service (stable DNS) + read service for replicas |
| `05-mysql-statefulset.yaml` | MySQL StatefulSet — 1 master + 2 replicas with ordered startup |

---

## The Scenario

```
Clients
   │
   ▼
dotnet-app (Deployment, 3 pods)  ──── writes ──▶  mysql-0 (MASTER)
                                  └── reads  ──▶  mysql-read Service
                                                       │
                                              mysql-1 (REPLICA)
                                              mysql-2 (REPLICA)
```

Client traffic hits .NET app pods. App sends **writes** to MySQL master (`mysql-0.mysql`).
App sends **reads** to `mysql-read` service which load-balances across replicas.

---

## Part 1 — Stateless App: Deployment (.NET)

### Scaling up to 3 pods

```bash
kubectl scale deployment dotnet-app --replicas=3
```

Each pod created is **identical and interchangeable**:
- Pod names: `dotnet-app-7d9f6b-xk2p1`, `dotnet-app-7d9f6b-m3nq8`, `dotnet-app-7d9f6b-wz5j4`
- Random hash suffix — no ordering, no identity
- One Service load-balances to **any** of them

### Scaling down to 2 pods

```bash
kubectl scale deployment dotnet-app --replicas=2
```

K8s picks any pod and kills it. No ceremony. No data loss. No coordination needed.
The surviving pods are still fully functional.

### Why is this easy?

| Property | .NET App Pod |
|----------|--------------|
| State stored? | No — state lives in MySQL |
| Pod identity needed? | No — any pod = same result |
| Order of creation? | Doesn't matter |
| Data survives pod death? | N/A — no local data |

---

## Part 2 — Stateful App: Why Deployment Fails for MySQL

Imagine deploying MySQL as a plain `Deployment` with `replicas: 3`.

**Problem 1: All 3 start simultaneously**
Replica pods need to clone data from master *before* they start accepting queries.
If they all boot at once, there is nothing to clone from yet.

**Problem 2: Random pod names = no stable address**
Replication needs to point to a specific master:
```sql
CHANGE MASTER TO MASTER_HOST='???';
```
With Deployment, pod IPs change on every restart. You can't hardcode an IP.
With a Service you get load-balanced to *any* pod — you might configure replication against a replica, causing split-brain.

**Problem 3: Shared or lost storage**
A Deployment PVC is shared among all pods (ReadWriteMany) or one pod gets it.
When pod restarts and lands on a different node, it may not get the same PVC — data is gone or wrong.

**Problem 4: Scale-down deletes wrong node**
If K8s randomly kills `mysql-1` instead of `mysql-2`, and mysql-2 was a mid-sync replica, the cluster is in an inconsistent state.

---

## Part 3 — StatefulSet Fixes Every Problem

### What StatefulSet gives you

```
mysql-0  →  mysql-1  →  mysql-2
  ▲             ▲            ▲
  │             │            │
MASTER       REPLICA      REPLICA
  │             │            │
data-mysql-0  data-mysql-1  data-mysql-2
(PVC)          (PVC)         (PVC)
```

#### 1. Stable, Predictable Pod Names

StatefulSet names pods with **ordinal index**, never random hashes:
```
mysql-0
mysql-1
mysql-2
```
These names are **permanent**. Pod dies → same name comes back.

#### 2. Stable DNS via Headless Service

With `clusterIP: None` (headless service), every pod gets its own DNS entry:
```
mysql-0.mysql.default.svc.cluster.local  →  Pod mysql-0
mysql-1.mysql.default.svc.cluster.local  →  Pod mysql-1
mysql-2.mysql.default.svc.cluster.local  →  Pod mysql-2
```

Replication config becomes deterministic:
```sql
CHANGE MASTER TO MASTER_HOST='mysql-0.mysql';
```
This DNS entry always resolves to the master, regardless of which node it runs on or how many times it restarts.

#### 3. Ordered Creation (one by one)

StatefulSet starts pods in strict order and waits for each to be `Ready` before creating the next:
```
mysql-0 starts → becomes Ready
  ↓
mysql-1 starts → clones data from mysql-0 → becomes Ready
  ↓
mysql-2 starts → clones data from mysql-1 → becomes Ready
```

Init container `clone-mysql` uses pod ordinal to find its donor:
```bash
ORDINAL=$(hostname | awk -F'-' '{print $NF}')
DONOR_HOST="mysql-$((ORDINAL-1)).mysql"   # mysql-1 clones from mysql-0
```

#### 4. Ordered Deletion (reverse order)

Scale down removes pods in reverse: `mysql-2` first, then `mysql-1`.
Master (`mysql-0`) is **never touched first** — cluster stays stable.

#### 5. Dedicated PVC per Pod (volumeClaimTemplates)

`volumeClaimTemplates` creates one PVC per pod automatically:
```
data-mysql-0   →  bound to mysql-0 only
data-mysql-1   →  bound to mysql-1 only
data-mysql-2   →  bound to mysql-2 only
```

If `mysql-1` dies and reschedules to a different node, K8s rebinds `data-mysql-1` to the new pod.
**Data survives across restarts and node changes.**

If you delete a pod (not scale down), the PVC is **not deleted** — data is safe.

---

## Part 4 — Database Scaling: Master / Replica Architecture

This is the pattern shown in the diagram: `mysql-0` = master, `mysql-1` and `mysql-2` = replicas.

```
                  ┌─────────────┐
                  │  MASTER     │
  Writes ────────▶│  mysql-0    │
  Reads  ────────▶│             │──── binary log ──▶ replicated to workers
                  └─────────────┘
                         │
          ┌──────────────┴──────────────┐
          ▼                             ▼
   ┌─────────────┐               ┌─────────────┐
   │  WORKER     │               │  WORKER     │
   │  mysql-1    │               │  mysql-2    │
   │  read-only  │               │  read-only  │
   └─────────────┘               └─────────────┘
   data replicas                 data replicas
```

### Why master/replica?

| Reason | Explanation |
|--------|-------------|
| **Read throughput** | 3 nodes serve reads vs 1. Scale reads horizontally. |
| **High availability** | If master dies, a replica can be promoted |
| **Backup** | Take backup from replica — master never pauses |
| **Only one writer** | One master prevents write conflicts and split-brain |

### Replication flow

1. App writes to `mysql-0` (master)
2. Master records change in **binary log**
3. Replica's IO thread pulls the binary log
4. Replica's SQL thread replays the log → data synchronized

---

## Part 5 — Replicating Stateful Apps is Complex

As shown in the second diagram, Kubernetes helps — but **you still do significant work**:

| Task | Who does it |
|------|------------|
| Ordered pod startup | StatefulSet handles automatically |
| Stable pod DNS | Headless service handles |
| Dedicated PVC per pod | `volumeClaimTemplates` handles |
| **Data cloning on new replica** | You write the init container logic |
| **Configure MySQL replication** | You write the `CHANGE MASTER TO` logic |
| **Make remote storage available** | You configure StorageClass + PV provisioner |
| **Backup strategy** | You configure cron jobs or operators |

This is why production MySQL on K8s usually uses an **operator** (e.g., MySQL Operator, Percona XtraDB Operator) — it encodes all this manual logic into a higher-level CRD.

---

## Part 6 — Deployment vs StatefulSet Comparison

| | Deployment | StatefulSet |
|---|---|---|
| **Pod names** | Random hash (`app-7d9f-xk2p`) | Ordered index (`mysql-0`, `mysql-1`) |
| **Pod identity** | None — interchangeable | Sticky — same name survives restart |
| **DNS per pod** | No | Yes (via headless service) |
| **Storage** | Shared PVC or none | Individual PVC per pod (`volumeClaimTemplates`) |
| **Startup order** | All at once (parallel) | Sequential, ordered (0 → 1 → 2) |
| **Shutdown order** | Random | Reverse order (2 → 1 → 0) |
| **Scale up** | Any pod anywhere | New pod gets next ordinal, clones from predecessor |
| **Scale down** | Kills any pod | Kills highest ordinal first |
| **Use for** | Web servers, APIs, workers | Databases, message queues, distributed systems |

---

## Apply Order

```bash
# 1. Secrets and config first
kubectl apply -f 02-mysql-secret.yaml
kubectl apply -f 03-mysql-configmap.yaml

# 2. Services (StatefulSet needs headless service to exist first)
kubectl apply -f 04-mysql-headless-service.yaml

# 3. StatefulSet (will create pods mysql-0 → mysql-1 → mysql-2 in order)
kubectl apply -f 05-mysql-statefulset.yaml

# 4. Wait for all mysql pods to be Running and Ready
kubectl rollout status statefulset/mysql

# 5. Deploy the .NET app
kubectl apply -f 01-dotnet-deployment.yaml
```

---

## Verify

```bash
# Watch StatefulSet pods come up in order
kubectl get pods -l app=mysql -w

# Check each pod's hostname (proves stable identity)
kubectl exec mysql-0 -- hostname
kubectl exec mysql-1 -- hostname
kubectl exec mysql-2 -- hostname

# Check dedicated PVCs created per pod
kubectl get pvc

# Check replication status on replica
kubectl exec mysql-1 -- mysql -uroot -prootpassword -e "SHOW SLAVE STATUS\G"

# Check .NET app pods
kubectl get pods -l app=dotnet-app

# Scale .NET app — easy, no ordering concern
kubectl scale deployment dotnet-app --replicas=5

# Scale MySQL — StatefulSet handles ordered scale-down safely
kubectl scale statefulset mysql --replicas=2
# mysql-2 is removed first, mysql-0 (master) stays untouched
```
