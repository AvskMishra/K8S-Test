# Example 2 — Kubernetes Namespaces

## What This Does

Demonstrates namespace isolation by deploying the same app (`webapp`) in two separate environments (`dev` and `prod`) within one cluster, plus a shared `monitoring` namespace accessible from both.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                          K8s Cluster                                 │
│                                                                      │
│  ┌─────────────────┐  ┌─────────────────┐  ┌───────────────────┐   │
│  │   dev           │  │   prod          │  │   monitoring      │   │
│  │                 │  │                 │  │                   │   │
│  │  webapp         │  │  webapp         │  │  metrics-collector│   │
│  │  (1 replica)    │  │  (3 replicas)   │  │  (prometheus)     │   │
│  │                 │  │                 │  │                   │   │
│  │  webapp-service │  │  webapp-service │  │  metrics-service  │   │
│  │  (ClusterIP)    │  │  (LoadBalancer) │  │  (ClusterIP)      │   │
│  │                 │  │                 │  │                   │   │
│  │  quota:         │  │  quota:         │  │  reachable from   │   │
│  │  5 pods max     │  │  20 pods max    │  │  dev AND prod via │   │
│  │  1Gi mem max    │  │  8Gi mem max    │  │  full DNS name    │   │
│  └─────────────────┘  └─────────────────┘  └───────────────────┘   │
│                                                                      │
│  Namespaces are ISOLATED — same name, no conflict                   │
│  Cross-namespace: use full DNS  svc.namespace.svc.cluster.local     │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Files

| File | Kind | Namespace | Purpose |
|------|------|-----------|---------|
| `00-namespaces.yaml` | Namespace ×3 | — | Creates `dev`, `prod`, `monitoring` namespaces |
| `01-resource-quota.yaml` | ResourceQuota ×2 | `dev`, `prod` | Limits CPU/memory/pods per namespace |
| `02-dev-app.yaml` | Deployment + Service | `dev` | `webapp` — 1 replica, ClusterIP |
| `03-prod-app.yaml` | Deployment + Service | `prod` | `webapp` — 3 replicas, LoadBalancer |
| `04-monitoring-shared.yaml` | Deployment + Service | `monitoring` | Prometheus — shared by dev and prod |

---

## Key Concepts

### Namespace — logical isolation
Each namespace is a separate scope within one cluster. Resources in different namespaces don't collide even with identical names.

```
dev/webapp   ≠   prod/webapp   (same name, different namespace — no conflict)
```

Default namespaces Kubernetes ships with:
| Namespace | Purpose |
|-----------|---------|
| `default` | Where resources go if you don't specify a namespace |
| `kube-system` | K8s internal components (scheduler, CoreDNS, etc.) |
| `kube-public` | Readable by all — used for cluster info |
| `kube-node-lease` | Node heartbeat objects |

---

### ResourceQuota — prevent resource starvation
Without quotas, one namespace can consume all cluster CPU/memory and starve others.

| Quota | dev | prod |
|-------|-----|------|
| Max pods | 5 | 20 |
| CPU requests | 500m | 2 cores |
| Memory requests | 512Mi | 4Gi |
| CPU limits | 1 core | 4 cores |
| Memory limits | 1Gi | 8Gi |

---

### Same app, different config per namespace

| Setting | dev | prod |
|---------|-----|------|
| Replicas | 1 | 3 |
| Service type | ClusterIP (internal) | LoadBalancer (external) |
| CPU limit | 100m | 250m |
| Memory limit | 128Mi | 256Mi |
| `APP_ENV` | `development` | `production` |

---

### Cross-namespace DNS
Services are namespace-scoped. To reach a service in a different namespace, use the full DNS name:

```
<service-name>.<namespace>.svc.cluster.local
```

Examples:
```
webapp-service.dev.svc.cluster.local          → dev webapp
webapp-service.prod.svc.cluster.local         → prod webapp
metrics-service.monitoring.svc.cluster.local  → Prometheus
```

Within the same namespace, short name works: `metrics-service:9090`

---

## Apply Order

Namespaces must exist before any resource is deployed into them:

```
1. 00-namespaces.yaml      (create dev, prod, monitoring)
2. 01-resource-quota.yaml  (quotas scoped to dev and prod)
3. 02-dev-app.yaml         (webapp in dev)
4. 03-prod-app.yaml        (webapp in prod)
5. 04-monitoring-shared.yaml (prometheus in monitoring)
```

---

## Concepts Demonstrated

- **Namespace** — logical cluster within a cluster, isolated scope
- **ResourceQuota** — cap CPU/memory/pods per namespace
- **Same name, different namespace** — no collision, completely isolated
- **ClusterIP vs LoadBalancer** — internal-only (dev) vs external (prod)
- **Cross-namespace DNS** — full DNS name to reach services across namespaces
- **Resource requests/limits** — per-container CPU and memory bounds

---

## Deep Dive

See [05-namespace-commands.md](05-namespace-commands.md) for quick-reference commands.  
See [COMMANDS.md](COMMANDS.md) for full deploy, verify, debug, and cleanup sequence.
