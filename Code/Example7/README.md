# Example 7 — Kubernetes Service Types

## Files

| File | What it shows |
|------|---------------|
| `01-sample-app-deployment.yaml` | `web-app` (Nginx) and `backend-api` pods for the examples |
| `02-clusterip-service.yaml` | ClusterIP — internal-only virtual IP with load balancing |
| `03-headless-service.yaml` | Headless — no VIP, per-pod DNS, StatefulSet use case |
| `04-nodeport-service.yaml` | NodePort — external access via port on every node |
| `05-loadbalancer-service.yaml` | LoadBalancer — cloud LB with public IP |
| `06-all-services-comparison.yaml` | All 4 types side-by-side on same app |

---

## The Big Picture

Kubernetes Services solve one problem: **pods are ephemeral**. They die, restart, and get new IPs constantly. A Service is a stable endpoint that always points to the right pods.

There are 4 types, each solving a different access problem:

```
                            ┌─────────────────────────────────────┐
                            │           CLUSTER BOUNDARY          │
                            │                                      │
  Internet ─────────────────┼──► LoadBalancer ──► pods            │
                            │                                      │
  NodeIP:30080 ─────────────┼──► NodePort     ──► pods            │
                            │                                      │
                            │    ClusterIP    ──► pods (internal)  │
                            │                                      │
                            │    Headless     ──► pod IPs (direct) │
                            │                                      │
                            └─────────────────────────────────────┘
```

---

## Service Types Deep Dive

### 1. ClusterIP (Default)

**What:** Virtual IP reachable only inside the cluster. kube-proxy load-balances across pods.

**DNS name auto-created:**
```
<service-name>.<namespace>.svc.cluster.local
backend-api.default.svc.cluster.local  → 10.96.45.12
```

**How traffic flows:**
```
Pod A  →  ClusterIP:8080  →  [iptables/IPVS rule]  →  pod-1 | pod-2 | pod-3
```

**Use when:**
- Microservice A needs to call microservice B (internal)
- Fronting a Deployment so pod restarts don't break callers
- DB, cache, or API only used inside the cluster

**Don't use when:**
- External users need access → NodePort or LoadBalancer
- Per-pod addressing needed → Headless

---

### 2. Headless Service (`clusterIP: None`)

**What:** No virtual IP assigned. DNS query returns individual pod IPs directly. CoreDNS returns an A-record per pod.

**DNS difference:**

| Query | ClusterIP result | Headless result |
|-------|-----------------|-----------------|
| `db.default.svc.cluster.local` | `10.96.1.5` (VIP) | `10.244.1.5, 10.244.2.7, 10.244.3.2` |

**StatefulSet bonus — per-pod stable DNS:**
```
db-0.db.default.svc.cluster.local  → always pod-0 IP
db-1.db.default.svc.cluster.local  → always pod-1 IP
db-2.db.default.svc.cluster.local  → always pod-2 IP
```

**Use when:**
- StatefulSet databases (MySQL, Postgres, Kafka, Cassandra)
- App needs to connect to a SPECIFIC pod (master vs replica)
- Client-side load balancing (gRPC, Cassandra driver peer discovery)

**Don't use when:**
- You want transparent load balancing → ClusterIP
- External access needed → NodePort/LoadBalancer

---

### 3. NodePort

**What:** Opens the same port (30000–32767 range) on **every node** in the cluster. External traffic → any node on that port → pods.

**Three ports:**

```
External: NodeIP:30080
             │
             ▼
Service port: 80      (ClusterIP still exists internally)
             │
             ▼
Target port: 80       (container port)
```

**Use when:**
- Local dev/testing (minikube, kind)
- On-premise clusters with no cloud load balancer
- CI/CD pipelines need external access

**Don't use when:**
- Production on cloud → LoadBalancer is better (HA, stable IP)
- Internal-only service → ClusterIP (NodePort wastes a port)

---

### 4. LoadBalancer

**What:** Asks the cloud provider to provision a real external load balancer (AWS ELB, GCP GLB, Azure LB). That LB gets a public IP/hostname and forwards to nodes, then to pods. Superset of NodePort + ClusterIP.

**How traffic flows:**
```
Internet
   │
   ▼
Cloud LB  (public IP, health-checks nodes)
   │
   ▼
NodePort on healthy nodes  (auto-assigned, hidden)
   │
   ▼  kube-proxy
pod-1 | pod-2 | pod-3
```

**Use when:**
- Production on cloud (GKE, EKS, AKS)
- TCP/UDP services that can't use Ingress (non-HTTP)
- Stable public IP needed

**Don't use when:**
- HTTP/S apps — use **Ingress** instead (one LB for many services, saves cost)
- Bare metal (no cloud provider) — use MetalLB or NodePort
- Internal services — use ClusterIP

---

## Comparison Table

| Feature | ClusterIP | Headless | NodePort | LoadBalancer |
|---------|-----------|----------|----------|--------------|
| Virtual IP assigned | Yes | **No** | Yes | Yes |
| Accessible externally | No | No | Yes (NodeIP:port) | Yes (public IP) |
| kube-proxy load balances | Yes | **No** | Yes | Yes |
| Per-pod DNS | No | **Yes** (StatefulSet) | No | No |
| Cloud required | No | No | No | **Yes** |
| Cost | Free | Free | Free | Paid (cloud LB) |
| Best for | Internal microservices | Stateful apps, DB | Dev/bare metal | Prod on cloud |

---

## Quick Decision Guide

```
Need external access?
    No  → ClusterIP
    Yes →
        Is it HTTP/S with multiple services?
            Yes → Ingress (sits in front of ClusterIP services)
            No  →
                Cloud cluster?
                    Yes → LoadBalancer
                    No  → NodePort

Is it a StatefulSet (DB, Kafka)?
    Need per-pod addressing?
        Yes → Headless  (+ optional ClusterIP for read traffic)
        No  → ClusterIP
```

---

## Commands

```bash
# Deploy sample app
kubectl apply -f 01-sample-app-deployment.yaml

# Apply individual service type
kubectl apply -f 02-clusterip-service.yaml
kubectl apply -f 03-headless-service.yaml
kubectl apply -f 04-nodeport-service.yaml
kubectl apply -f 05-loadbalancer-service.yaml

# Or apply all types at once (comparison)
kubectl apply -f 06-all-services-comparison.yaml

# List all services — compare CLUSTER-IP and EXTERNAL-IP columns
kubectl get svc

# Inspect ClusterIP assigned
kubectl get svc backend-api -o wide

# See Endpoints (pod IPs behind a service)
kubectl get endpoints backend-api
kubectl get endpoints web-app-headless   # returns pod IPs directly

# Verify headless DNS returns multiple IPs (run from inside a pod)
kubectl run tmp --image=busybox --rm -it -- nslookup web-app-headless

# Verify ClusterIP DNS (single VIP)
kubectl run tmp --image=busybox --rm -it -- nslookup web-app-clusterip

# Check LoadBalancer external IP (takes ~1-2 min on cloud)
kubectl get svc web-app-lb --watch

# Access NodePort (minikube)
minikube service web-app-nodeport --url

# Delete all
kubectl delete -f .
```

---

## Key Concept: Why Stable DNS Matters

Pods die constantly. IP `10.244.1.5` (pod-1) becomes `10.244.3.8` after a restart.

Services give you a **stable name** (`backend-api.default.svc.cluster.local`) that always resolves to healthy pods — regardless of restarts, scaling, or rescheduling.

Without Services, every microservice would need to track pod IPs manually. With Services, you just call `http://backend-api` and Kubernetes handles the rest.
