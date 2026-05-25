# Example 1 — MongoDB + Mongo Express on Kubernetes

## What This Does

Deploys a MongoDB database with a Mongo Express web UI inside a local Minikube cluster.

```
Browser ──► (minikube IP:30000)
                │
                ▼
    ┌─────────────────────────┐
    │  mongoexpress-service   │  ← LoadBalancer, nodePort 30000
    │  (port 8081)            │
    └────────────┬────────────┘
                 │
                 ▼
    ┌─────────────────────────┐
    │  Mongo Express Pod      │  ← reads credentials from Secret
    │  (mongo-express:1.0.2)  │    reads DB host from ConfigMap
    └────────────┬────────────┘
                 │  connects to mongodb-service:27017
                 ▼
    ┌─────────────────────────┐
    │  mongodb-service        │  ← ClusterIP (internal only)
    │  (port 27017)           │
    └────────────┬────────────┘
                 │
                 ▼
    ┌─────────────────────────┐
    │  MongoDB Pod            │  ← credentials injected from Secret
    │  (mongo:6.0)            │
    └─────────────────────────┘
```

---

## Files

| File | Kind | Purpose |
|------|------|---------|
| `mongo-secret.yaml` | Secret | Base64-encoded MongoDB credentials |
| `mongo-configmap.yaml` | ConfigMap | MongoDB service URL for Mongo Express |
| `mongo-deployment.yaml` | Deployment | MongoDB pod |
| `mongo-service.yaml` | Service (ClusterIP) | Internal access to MongoDB |
| `mongoexpress-deployment.yaml` | Deployment + Service | Mongo Express pod + LoadBalancer |

---

## Architecture: Key Concepts

### Secret — sensitive data
MongoDB credentials stored as base64-encoded values. Both pods read from the same Secret — single source of truth for credentials.

### ConfigMap — non-sensitive config
Stores the MongoDB service DNS name (`mongodb-service`). Mongo Express reads this to know where to connect.

### ClusterIP Service (MongoDB)
MongoDB is NOT exposed externally. Only reachable from inside the cluster. Mongo Express talks to it via the internal DNS name `mongodb-service`.

### LoadBalancer Service (Mongo Express)
Mongo Express IS exposed externally via `nodePort: 30000`. On Minikube, access via `minikube service mongoexpress-service`.

---

## Apply Order

**Order matters.** Secret and ConfigMap must exist before pods start — pods read these at container creation.

```
1. mongo-secret.yaml       (Secret must exist before any pod starts)
2. mongo-configmap.yaml    (ConfigMap must exist before Mongo Express starts)
3. mongo-deployment.yaml   (MongoDB pod)
4. mongo-service.yaml      (Internal DNS for MongoDB)
5. mongoexpress-deployment.yaml  (Mongo Express pod + external service)
```

---

## Access

```
Mongo Express UI: minikube service mongoexpress-service
```

Login credentials (from the Secret):
- Username: `mongouser`
- Password: `mongopass`

---

## Concepts Demonstrated

- **Secret** — store credentials safely, not in Deployment YAML
- **ConfigMap** — externalise configuration (service URLs, settings)
- **ClusterIP** — internal-only service (database)
- **LoadBalancer** — external-access service (UI)
- **`secretKeyRef`** — inject Secret value as environment variable
- **`configMapKeyRef`** — inject ConfigMap value as environment variable
- **Apply order** — dependencies must be created before consumers

---

## Deep Dive

See [YAML-EXPLAINED.md](YAML-EXPLAINED.md) for field-by-field breakdown of every key in every file.  
See [COMMANDS.md](COMMANDS.md) for all kubectl commands to deploy, verify, and debug.
