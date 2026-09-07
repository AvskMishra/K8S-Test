# Cluster Status Snapshot — `k8slab`

Captured: Phase 1 (base cluster), shortly after creation. Re-run the commands at the bottom to refresh this anytime.

## Nodes

| Name | Role | Status | Version | Internal IP | Container Runtime |
|---|---|---|---|---|---|
| k8slab | control-plane | Ready | v1.37.0 | 192.168.49.2 | cri-o://1.35.7 |
| k8slab-m02 | worker | Ready | v1.37.0 | 192.168.49.3 | cri-o://1.35.7 |
| k8slab-m03 | worker | Ready | v1.37.0 | 192.168.49.4 | cri-o://1.35.7 |

All nodes run Debian GNU/Linux 12 (bookworm) on kernel `5.10.16.3-microsoft-standard-WSL2`.

## Pods (namespace: kube-system)

| Pod | Node | Status | Restarts | What it does |
|---|---|---|---|---|
| etcd-k8slab | k8slab | Running | 0 | Cluster's key-value store — holds all Kubernetes state |
| kube-apiserver-k8slab | k8slab | Running | 0 | Front door for the cluster API — every kubectl command hits this |
| kube-controller-manager-k8slab | k8slab | Running | 0 | Runs control loops (node, replication, etc.) that keep actual state matching desired state |
| kube-scheduler-k8slab | k8slab | Running | 0 | Decides which node a new pod lands on |
| coredns-559f6c778d-9z5h9 | k8slab | Running | 0 | Cluster-internal DNS — lets pods find Services by name |
| storage-provisioner | k8slab | Running | 0 | minikube's default dynamic volume provisioner (hostPath-backed) |
| kindnet-8569z | k8slab | Running | 0 | CNI plugin — gives pods on this node network connectivity |
| kindnet-c7c8l | k8slab-m02 | Running | 0 | CNI plugin (same, per-node) |
| kindnet-76cdl | k8slab-m03 | Running | 0 | CNI plugin (same, per-node) |
| kube-proxy-c7hpn | k8slab | Running | 0 | Programs Service routing rules on this node |
| kube-proxy-q66zg | k8slab-m02 | Running | 0 | Same, per-node |
| kube-proxy-sbbkq | k8slab-m03 | Running | 0 | Same, per-node |

**Pattern to notice:** control-plane pods (etcd, apiserver, controller-manager, scheduler) only run on `k8slab`. `kindnet` and `kube-proxy` are DaemonSets — one copy per node, on every node including workers. This is normal and will be the baseline every later phase builds on (e.g. Cilium in phase 3 will *replace* the `kindnet` DaemonSet with its own).

No application workloads are deployed yet — this is purely the cluster's own control and networking plane.

## How to refresh this snapshot

Run from Windows PowerShell:
```powershell
wsl -d Ubuntu -u root -- sh -c "kubectl get nodes -o wide; echo; kubectl get pods -A -o wide"
```
