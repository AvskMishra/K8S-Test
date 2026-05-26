# Example 5 — Persistent Volumes, PVCs, StorageClasses, ConfigMaps & Secrets

## What This Example Covers

| File | What it shows |
|------|---------------|
| `01-local-pv.yaml` | Manual PV backed by node's local disk |
| `02-local-pvc.yaml` | PVC that binds to the local PV |
| `03-nfs-pv.yaml` | Manual PV backed by a remote NFS share |
| `04-nfs-pvc.yaml` | PVC that binds to the NFS PV |
| `05-cloud-storageclass.yaml` | StorageClass for AWS EBS, GCP PD, Azure Disk |
| `06-cloud-pvc-dynamic.yaml` | Dynamic PVC — PV auto-created by StorageClass |
| `07-app-configmap.yaml` | ConfigMap: non-sensitive app config |
| `08-app-secret.yaml` | Secret: passwords, keys, TLS certs |
| `09-app-deployment.yaml` | Deployment wiring PVC + ConfigMap + Secret together |

---

## Core Concepts

### PersistentVolume (PV)

A **PV** is a piece of storage in the cluster, provisioned by an admin (or auto-provisioned by a StorageClass).

- It exists independently of any pod
- It has a lifecycle separate from the pods that use it
- It can be backed by: local disk, NFS, AWS EBS, GCP PD, Azure Disk, Ceph, etc.

```
PV = actual storage resource (like a hard drive the cluster knows about)
```

### PersistentVolumeClaim (PVC)

A **PVC** is a **request** for storage by a user/developer.

- The developer does not need to know *where* the storage is
- K8s matches (binds) the PVC to a suitable PV automatically
- The pod then uses the PVC — not the PV directly

```
PVC = "I need 10Gi of ReadWriteOnce storage" → K8s finds a matching PV → bound
```

---

## The Full Flow: Admin → PV → PVC → Pod

```
┌─────────────────────────────────────────────────────────────────┐
│  ADMIN ROLE (cluster operator)                                  │
│                                                                 │
│  1. Creates StorageClass (or manual PV)                         │
│     "Here is a pool of AWS EBS disks, gp3, encrypted"          │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│  KUBERNETES CONTROL PLANE                                       │
│                                                                 │
│  2a. (Static)  Admin manually created PV exists in cluster      │
│  2b. (Dynamic) StorageClass auto-creates PV when PVC appears    │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│  DEVELOPER / K8S USER                                           │
│                                                                 │
│  3. Creates PVC: "I need 50Gi, ReadWriteOnce, StorageClass=ebs" │
│                                                                 │
│  4. K8s binds PVC → PV (checks: capacity, accessMode, class)   │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────────┐
│  POD                                                            │
│                                                                 │
│  5. Pod references PVC in volumes:                              │
│       volumes:                                                  │
│         - name: data                                            │
│           persistentVolumeClaim:                                │
│             claimName: my-pvc                                   │
│                                                                 │
│  6. K8s mounts the PV's actual disk into the pod's filesystem  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Storage Types Compared

### Local Storage (`01-local-pv.yaml`)

```yaml
local:
  path: /mnt/data/local-pv
nodeAffinity:
  required:
    nodeSelectorTerms:
      - matchExpressions:
          - key: kubernetes.io/hostname
            operator: In
            values:
              - minikube
```

| Property | Value |
|----------|-------|
| Speed | Fastest (direct disk, no network) |
| Access modes | `ReadWriteOnce` only |
| Node pinned | YES — pod always schedules on same node |
| Survives node death | NO — data lost if node dies |
| Use case | Databases needing low latency (Cassandra, etcd) |

**Gotcha:** If the node goes down, the pod cannot reschedule elsewhere. Data is stuck.

---

### NFS Storage (`03-nfs-pv.yaml`)

```yaml
nfs:
  server: 192.168.1.100
  path: /exports/k8s-shared
```

| Property | Value |
|----------|-------|
| Speed | Medium (network overhead) |
| Access modes | `ReadWriteMany` — multiple pods on multiple nodes |
| Node pinned | NO — any node can mount |
| Survives node death | YES — data on NFS server |
| Use case | Shared uploads, media, logs across replicas |

**Gotcha:** NFS server becomes a single point of failure. NFS needs to be installed on all nodes (`apt install nfs-common`).

---

### Cloud Storage — Dynamic (`05-cloud-storageclass.yaml` + `06-cloud-pvc-dynamic.yaml`)

```yaml
storageClassName: aws-ebs-gp3   # PVC references this
# No PV created manually — StorageClass + CSI driver creates it
```

| Property | AWS EBS (gp3) | GCP PD SSD | Azure Premium Disk |
|----------|---------------|------------|-------------------|
| Speed | High | High | High |
| Access modes | `ReadWriteOnce` | `ReadWriteOnce` | `ReadWriteOnce` |
| Node pinned | NO (zone pinned) | NO (zone pinned) | NO (zone pinned) |
| Survives node death | YES | YES | YES |
| Auto-provisioned | YES | YES | YES |
| Resize support | YES | YES | YES |

**Gotcha:** Cloud disks are zone-scoped. Pod must schedule in the same AZ as disk. `WaitForFirstConsumer` binding mode handles this correctly.

---

## StorageClass Deep Dive

StorageClass is the admin's template that tells K8s **how** to create storage dynamically.

```
StorageClass = provisioner + parameters + reclaim policy
```

### Key Fields

```yaml
provisioner: ebs.csi.aws.com        # Which CSI driver to call
volumeBindingMode: WaitForFirstConsumer  # or Immediate
reclaimPolicy: Delete               # or Retain
allowVolumeExpansion: true
parameters:
  type: gp3                         # Driver-specific options
```

### `volumeBindingMode`

| Mode | Behavior |
|------|----------|
| `Immediate` | PV created as soon as PVC appears (ignores pod scheduling) |
| `WaitForFirstConsumer` | PV created only when pod is being scheduled (respects node/zone) |

Always use `WaitForFirstConsumer` for cloud disks to avoid zone mismatch.

### `reclaimPolicy`

| Policy | When PVC deleted |
|--------|-----------------|
| `Delete` | PV and underlying disk destroyed |
| `Retain` | PV stays, must be manually reclaimed by admin |
| `Recycle` | PV wiped (`rm -rf`), made available again (deprecated) |

### Static vs Dynamic Provisioning

```
STATIC (manual PV):
  Admin creates PV → Developer creates PVC → K8s binds them

DYNAMIC (StorageClass):
  Admin creates StorageClass → Developer creates PVC →
  K8s calls CSI driver → driver creates actual disk → PV auto-created → bound
```

---

## Access Modes

| Mode | Short | Multiple Pods | Multiple Nodes |
|------|-------|--------------|----------------|
| ReadWriteOnce | RWO | Same node only | NO |
| ReadOnlyMany | ROX | Any node | YES (read-only) |
| ReadWriteMany | RWX | Any node | YES |
| ReadWriteOncePod | RWOP | Single pod only | NO |

- Local disk → RWO only
- NFS → RWX capable
- Cloud EBS/PD → RWO only (one node at a time)

---

## ConfigMap vs Secret: Managing Config for Multiple Pods

Both ConfigMap and Secret are **namespace-scoped** objects. Any pod in the same namespace can reference them by name. With 3 replicas in a Deployment, all 3 pods share the same ConfigMap/Secret — no duplication needed.

### ConfigMap (`07-app-configmap.yaml`)

Stores **non-sensitive** configuration.

```yaml
# Two ways to consume:

# 1. As environment variables (entire ConfigMap)
envFrom:
  - configMapRef:
      name: app-config        # All keys → env vars

# 2. As a mounted file
volumes:
  - name: config-file
    configMap:
      name: app-config
volumeMounts:
  - mountPath: /etc/app/app.properties
    name: config-file
    subPath: app.properties   # Mount single key as single file
```

**When ConfigMap updates**, mounted file volumes update automatically (within ~1 min). Env vars do NOT update — pod restart required.

### Secret (`08-app-secret.yaml`)

Stores **sensitive** data. Values are base64 encoded (not encrypted by default — enable etcd encryption at rest in production).

```yaml
# Encode a value:
# echo -n "mypassword" | base64  →  bXlwYXNzd29yZA==

# Two ways to consume:

# 1. As env var (single key)
env:
  - name: DB_PASSWORD
    valueFrom:
      secretKeyRef:
        name: app-secret
        key: DB_PASSWORD

# 2. As mounted files (more secure — not visible in kubectl describe pod)
volumes:
  - name: secrets-vol
    secret:
      secretName: app-secret
      defaultMode: 0400       # chmod 400 — owner read-only
volumeMounts:
  - mountPath: /etc/secrets
    name: secrets-vol
    readOnly: true
```

### ConfigMap vs Secret Comparison

| | ConfigMap | Secret |
|--|-----------|--------|
| Data encoding | Plain text | base64 |
| Encryption at rest | No | Optional (enable in etcd) |
| RBAC restrictions | Standard | Stricter (recommended) |
| Use for | DB host, log level, ports | Passwords, API keys, certs |
| Show in `kubectl describe` | YES | Values hidden |
| Mount as file | YES | YES |
| Mount as env var | YES | YES |

### Multi-Pod Behavior

```
Deployment (3 replicas)
├── Pod-1  ─┐
├── Pod-2  ─┼─── all read same ConfigMap + Secret from namespace
└── Pod-3  ─┘

No copying. No duplication.
K8s injects the same object into every pod via kubelet.
```

---

## How Pod Uses PVC + ConfigMap + Secret Together (`09-app-deployment.yaml`)

```
Pod filesystem layout after all mounts:

/data/uploads/          ← PVC (persistent, survives pod restart)
/etc/app/app.properties ← ConfigMap (config file)
/etc/secrets/           ← Secret files
  DB_PASSWORD
  DB_USER
  AWS_ACCESS_KEY_ID

Environment variables:
  APP_ENV=production    ← from ConfigMap (envFrom)
  LOG_LEVEL=info        ← from ConfigMap (envFrom)
  DB_PASSWORD=***       ← from Secret (env.valueFrom)
```

---

## How to Apply (Local/Minikube)

```bash
# 1. Prepare node directory (for local PV)
minikube ssh "sudo mkdir -p /mnt/data/local-pv && sudo chmod 777 /mnt/data/local-pv"

# 2. Apply in order
kubectl apply -f 01-local-pv.yaml
kubectl apply -f 02-local-pvc.yaml

# 3. Check binding
kubectl get pv,pvc

# 4. Apply config and secrets
kubectl apply -f 07-app-configmap.yaml
kubectl apply -f 08-app-secret.yaml

# 5. Deploy app
kubectl apply -f 09-app-deployment.yaml

# 6. Verify mounts inside pod
kubectl exec -it <pod-name> -- ls /data/uploads
kubectl exec -it <pod-name> -- cat /etc/app/app.properties
kubectl exec -it <pod-name> -- ls /etc/secrets

# 7. Check PVC status (should say Bound)
kubectl get pvc
kubectl describe pvc local-pvc
```

## For NFS

```bash
# On NFS server
sudo apt install nfs-kernel-server
sudo mkdir -p /exports/k8s-shared
echo "/exports/k8s-shared *(rw,sync,no_subtree_check,no_root_squash)" >> /etc/exports
sudo exportfs -a

# On each K8s node
sudo apt install nfs-common

kubectl apply -f 03-nfs-pv.yaml
kubectl apply -f 04-nfs-pvc.yaml
```

## For Cloud (AWS EBS)

```bash
# 1. Install AWS EBS CSI driver
kubectl apply -k "github.com/kubernetes-sigs/aws-ebs-csi-driver/deploy/kubernetes/overlays/stable/?ref=release-1.28"

# 2. Apply StorageClass
kubectl apply -f 05-cloud-storageclass.yaml

# 3. Create PVC — PV is auto-provisioned
kubectl apply -f 06-cloud-pvc-dynamic.yaml

# 4. Check — PVC will be Pending until pod is created (WaitForFirstConsumer)
kubectl get pvc cloud-pvc-dynamic
```

---

## Common Errors

| Error | Cause | Fix |
|-------|-------|-----|
| PVC stuck `Pending` | No matching PV (capacity/mode/class mismatch) | Check `kubectl describe pvc` — match storageClassName, accessModes, capacity |
| `Multi-Attach error` | RWO PV mounted on 2 nodes | Use RWX (NFS) or ensure single-node pod |
| `node(s) didn't match node selector` | Local PV nodeAffinity mismatch | Check node hostname: `kubectl get nodes` |
| Secret base64 error | Value not properly encoded | `echo -n "value" \| base64` — note the `-n` flag |
| ConfigMap not updating | Using envFrom (env vars cache) | Use volume mount for live reload, or restart pod |
