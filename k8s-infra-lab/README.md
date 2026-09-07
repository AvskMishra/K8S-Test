# Kubernetes Platform Lab — Planning, Execution & Reasoning Log

**Goal:** build a local, hands-on Kubernetes platform to eventually run a .NET Core 10 + Angular 22 + MongoDB application, while deliberately learning each infrastructure layer one at a time: Kubernetes, CRI-O, Podman, minikube, Helm, Cilium, Longhorn, ArgoCD, Sealed Secrets, cert-manager, Istio, Argo Workflows, Argo Events, and MongoDB — plus load balancing, scaling, and backup concepts along the way.

This file is a living log. Each phase records **what** we did, **why**, and **what broke**, so you can look back and understand the reasoning, not just copy commands.

---

## Architecture

```
Windows 10 host (16GB RAM / 8 CPU)
  └─ WSL2 "Ubuntu" distro   (VM: 12GB RAM / 8 CPU, see .wslconfig)
       └─ Podman (rootful)   — container engine
            └─ minikube      — cluster orchestrator, driver=podman
                 ├─ k8slab      (control-plane node, CRI-O runtime)
                 ├─ k8slab-m02  (worker node, CRI-O runtime)
                 └─ k8slab-m03  (worker node, CRI-O runtime)
```

Everything runs **inside WSL2 Ubuntu**, not in native Windows Podman. See Phase 0 for why.

---

## Phase 0 — Environment troubleshooting (the part that ate the time budget)

This phase is kept in detail because the *reasoning* here is itself a good lesson in how container networking on WSL2 actually works.

### 0.1 — Native Windows Podman was broken

Checked `podman machine list`: a machine existed but had **never** successfully started (`LAST UP: Never`, 11 days old). Windows Podman runs a Fedora-based VM ("podman machine") inside WSL2. That VM needs **systemd + unified cgroup v2** to boot. This host's WSL2 platform (`wsl.exe` version `10.0.21996.1`, the old Windows-inbox build, not the modern Microsoft Store WSL) only provides **cgroup v1 hybrid mode** and has no native systemd support. Systemd inside the podman-machine VM exited instantly and silently on every boot attempt — that's why it never worked, even before we touched it.

**Decision:** abandon `podman machine` (the Windows-integration layer) entirely, and run Podman **natively inside the existing WSL2 "Ubuntu" distro** instead. Ubuntu IS a real Linux kernel environment already — no nested VM/systemd dance needed for Podman itself to run there.

### 0.2 — Sized WSL2 for the whole roadmap

Default WSL2 memory allocation was only 7.7GB (auto = 50% of host RAM). Since later phases (Cilium, Longhorn, Istio, ArgoCD, MongoDB) all add real memory pressure, raised the ceiling via `C:\Users\avskm\.wslconfig`:

```ini
[wsl2]
memory=12GB
processors=8
swap=4GB
localhostForwarding=true
```

Requires `wsl --shutdown` to take effect.

### 0.3 — Installed Podman inside Ubuntu, hit kernel NAT gap

`apt-get install podman` worked fine (Podman 4.9.3). But `podman run` with any bridge network failed:

```
Error: netavark: unable to append rule '-d 10.88.0.0/16 -j ACCEPT' to table 'nat':
code: 4, msg: iptables v1.8.10 (nf_tables): RULE_APPEND failed (No such file or directory)
```

Root cause: this WSL2 kernel build (`5.10.16.3-microsoft-standard-WSL2`) has incomplete **nf_tables NAT** support. Podman's default firewall path (`iptables-nft`, the nftables-compatibility shim) can't create the NAT rules it needs for bridge networking — and minikube's podman/docker driver **always** creates a dedicated bridge network for its nodes, so this blocked cluster creation entirely, rootless or not.

Tried the "correct" fix — updating to the modern Microsoft Store WSL package, which ships a kernel with full netfilter support. Downloaded it directly from `github.com/microsoft/WSL` releases, but installation failed:

```
Cannot register the ...WindowsSubsystemForLinux package.
Administrator privileges required to install packaged service
```

No admin rights available in this session. **Workaround found instead:** the kernel's *legacy* xtables NAT path (as opposed to the nftables-compat shim) works fine on this kernel — it was specifically the nftables translation layer that was broken, not NAT support itself. Fix:

```bash
update-alternatives --set iptables /usr/sbin/iptables-legacy
update-alternatives --set ip6tables /usr/sbin/ip6tables-legacy
```

Verified with a raw `iptables -t nat -A ... -j ACCEPT` test, then a real `podman network create` + bridge-networked container run — both succeeded. This is `scripts/00-fix-wsl-networking.sh`.

> If you ever do get admin access and run `wsl --update` as Administrator, this workaround likely becomes unnecessary — but it's harmless to leave in place either way.

### 0.4 — `/lib/modules` doesn't exist

minikube's podman driver bind-mounts `/lib/modules:/lib/modules:ro` into each node container. WSL2's kernel has no real module files (everything's compiled in, not loaded as `.ko` files), so `/lib/modules` doesn't exist on the host at all, and the mount failed with `statfs /lib/modules: no such file or directory`. Fixed by creating an empty stub: `mkdir -p /lib/modules/$(uname -r)`. Also in `scripts/00-fix-wsl-networking.sh`.

### 0.5 — Rootless Podman vs. the CRI-O kicbase image

Best practice is running Podman **rootless** (created a `dev` user with proper subuid/subgid ranges for this). Rootless networking uses `pasta`/`slirp4netns` (userspace network stack) which sidesteps kernel NAT concerns even more cleanly. This worked for plain containers.

But starting the actual minikube cluster as the rootless `dev` user failed differently:

```
Failed to enable unit, unit containerd-fuse-overlayfs.service does not exist.
```

minikube's kicbase image's init script assumes a systemd unit for fuse-overlayfs storage (needed by rootless containers lacking native overlayfs) that isn't present in the CRI-O variant of that image. This looks like a minikube/kicbase packaging gap specific to the podman+CRI-O+rootless combination, not something fixable from our side.

**Pragmatic decision:** run the minikube cluster as **root** inside WSL2 Ubuntu instead (`minikube start ... --force`, since minikube warns against podman-as-root by default). Since WSL2 Ubuntu here is a disposable, single-user local dev sandbox (not a shared or production host), the reduced isolation from running as root is an acceptable tradeoff for a working learning cluster. This sidesteps the rootless-only fuse-overlayfs codepath entirely.

---

## Phase 1 — Base cluster (DONE ✅)

**What:** a 3-node Kubernetes cluster — 1 control-plane + 2 workers — using Podman as the container engine, CRI-O as the Kubernetes container runtime, orchestrated by minikube.

**Command** (`scripts/02-create-cluster.sh`):
```bash
minikube start -p k8slab \
  --nodes=3 \
  --driver=podman \
  --container-runtime=crio \
  --cpus=2 \
  --memory=2500mb \
  --disk-size=15g \
  --force
```

**Verified result:**
```
NAME         STATUS   ROLES           VERSION   INTERNAL-IP    CONTAINER-RUNTIME
k8slab       Ready    control-plane   v1.37.0   192.168.49.2   cri-o://1.35.7
k8slab-m02   Ready    <none>          v1.37.0   192.168.49.3   cri-o://1.35.7
k8slab-m03   Ready    <none>          v1.37.0   192.168.49.4   cri-o://1.35.7
```

**Components running:** etcd, kube-apiserver, kube-controller-manager, kube-scheduler, kube-proxy, CoreDNS, kindnet (minikube's default CNI), storage-provisioner.

**Resource footprint:** 6 vCPU / 7.5GB RAM committed across the 3 node containers, inside the 8 vCPU / 12GB WSL2 VM — leaving roughly 2 vCPU / 4.5GB headroom for host/Podman overhead and whatever we add next.

**Tools installed (`scripts/01-install-tools.sh`):** Podman 4.9.3, minikube v1.39.0, kubectl v1.37.0.

### Operating this cluster day to day

Everything below runs **as root inside the WSL2 Ubuntu distro** (`wsl -d Ubuntu -u root -- <command>` from Windows, or open a shell with `wsl -d Ubuntu -u root`):

| Action | Command |
|---|---|
| Check status | `minikube status -p k8slab` |
| Stop (keep state) | `minikube stop -p k8slab` |
| Start again | `minikube start -p k8slab` |
| Delete entirely | `minikube delete -p k8slab` |
| Talk to the cluster | `kubectl get nodes` / `kubectl get pods -A` |
| Cluster IP IPs | `192.168.49.2` (control-plane), `.3`, `.4` (workers) |

**Persistence note:** the cluster's containers live inside Podman's storage inside the WSL2 Ubuntu distro's virtual disk. It survives a `wsl --shutdown` (WSL persists the disk), but **does not survive a Windows reboot cleanly unless you restart WSL and run `minikube start -p k8slab` again** — minikube doesn't auto-start on boot.

---

## Roadmap — remaining phases

Deliberately incremental, one concept at a time. Suggested order and *why* that order:

| # | Phase | What it teaches | Why this position in the order |
|---|---|---|---|
| 2 | **Helm** | Package management for Kubernetes | Nearly every later phase (Cilium, Longhorn, cert-manager, ArgoCD, MongoDB) installs cleanest via a Helm chart — install this first so later phases aren't blocked on it. |
| 3 | **Cilium** | Real CNI, replacing kindnet; NetworkPolicy; eBPF dataplane; LoadBalancer via L2 announcements (**Load Balancing** concept #1) | Networking is foundational — everything after (Istio, service exposure, MongoDB replica communication) sits on top of the CNI. Swapping kindnet → Cilium is itself a hands-on lesson in what a CNI actually does, since nodes go through a NotReady → Ready cycle you can watch. |
| 4 | **metrics-server + HPA** | **Scaling** concept: Horizontal Pod Autoscaler | Small, fast win once networking is solid — teaches core K8s scaling before bringing in heavier components. |
| 5 | **Longhorn** | Persistent, replicated block storage; volume snapshots/backups (**Backup** concept #1) | MongoDB (phase 12) needs real persistent storage with backup/restore, not minikube's default single-replica hostPath storage-provisioner. Doing this before MongoDB means the database phase is "just" a Helm install onto storage that already works. |
| 6 | **cert-manager** | Automated TLS certificate issuance/rotation | Needed before exposing anything through Istio's gateway (phase 8) or ArgoCD's UI (phase 9) over HTTPS. |
| 7 | **Sealed Secrets** | Encrypting secrets so they're safe to commit to Git | Must be in place *before* ArgoCD (phase 9), since GitOps means secrets need to live in Git safely from day one. |
| 8 | **Istio** | Service mesh: mTLS, L7 traffic management/**load balancing**, retries, observability | Bigger, heavier concept — deliberately placed after the storage/cert/secrets foundation is solid, since Istio sidecars touch every pod and are easier to debug once the rest of the platform is stable. |
| 9 | **ArgoCD** | GitOps continuous delivery | Once there's a real platform (storage, certs, secrets, mesh) worth deploying *declaratively*, ArgoCD's value becomes concrete rather than abstract. |
| 10 | **Argo Workflows** | Pipeline/job orchestration on Kubernetes | Builds on ArgoCD's GitOps model (same project family, shares CRD patterns). |
| 11 | **Argo Events** | Event-driven triggering of Workflows | Natural extension once Workflows exists — closes the loop from "event happens" → "pipeline runs". |
| 12 | **MongoDB** | Stateful workloads: StatefulSets, replica sets (**Scaling** concept #2 — scaling a database), backups onto Longhorn (**Backup** concept #2) | Last infra phase — by now storage, secrets, certs, mesh, and GitOps all exist to deploy it properly rather than as a bare pod. |
| 13 | **Application deployment** | .NET Core 10 API + Angular 22 frontend, wired to MongoDB, deployed via ArgoCD | The actual application — deliberately last, once the platform underneath is understood. |

**Cross-cutting concepts woven throughout** (not a single phase, but touched repeatedly as above):
- **Load Balancing:** ClusterIP/NodePort Services (already present) → Cilium LB IPAM / L2 announcements (phase 3) → Istio Gateway L7 load balancing (phase 8).
- **Scaling:** HPA for stateless workloads (phase 4) → StatefulSet replica scaling for MongoDB (phase 12) → (optional later) minikube `node add` for cluster-level scaling.
- **Backup:** Longhorn volume snapshots/backups (phase 5) → MongoDB-specific backup strategy on top of it (phase 12) → (optional later) Velero for whole-cluster backup as a capstone exercise.

---

## Next step

Say the word (or just say "phase 2") and we'll install Helm and use it to install our first real chart — small, fast, and sets up every phase after it.
