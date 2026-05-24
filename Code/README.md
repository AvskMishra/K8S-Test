# Kubernetes MongoDB + Mongo Express Setup

---

## Problem Statement

**Goal:** Deploy two pods in a Kubernetes cluster — MongoDB and Mongo Express — and connect them so the browser can access the Mongo Express UI which talks to MongoDB internally.

```
Browser
  │
  ▼
Mongo Express External Service   ← LoadBalancer (nodePort 30000)
  │
  ▼
Mongo Express Pod                ← reads DB URL from ConfigMap, creds from Secret
  │
  ▼
MongoDB Internal Service         ← ClusterIP (only reachable inside cluster)
  │
  ▼
MongoDB Pod                      ← creds injected from Secret (DB User + DB Pwd)
         ▲
         │
       Secret
    (DB User, DB Pwd)
```

**Components needed:**
- `Secret` — store MongoDB username & password securely
- `ConfigMap` — store MongoDB service URL (non-sensitive config)
- `MongoDB Deployment` — database pod
- `MongoDB Internal Service` — ClusterIP, exposes MongoDB inside cluster only
- `Mongo Express Deployment` — UI pod, connects to MongoDB via internal service
- `Mongo Express External Service` — LoadBalancer, exposes UI to browser

---

Two pods, one cluster. MongoDB as database node, Mongo Express as UI node. Connected via internal Service DNS.

---

## File Overview

| File | Purpose |
|------|---------|
| `mongo-secret.yaml` | Stores DB credentials (base64 encoded) |
| `mongo-configmap.yaml` | Stores MongoDB service URL (plain text config) |
| `mongo-deployment.yaml` | MongoDB pod — pulls creds from Secret |
| `mongo-service.yaml` | Internal ClusterIP — exposes MongoDB inside cluster |
| `mongoexpress-deployment.yaml` | Mongo Express pod + LoadBalancer service |

---

## Why Secret vs ConfigMap?

| | Secret | ConfigMap |
|--|--------|-----------|
| Use for | Passwords, tokens, keys | URLs, config values, non-sensitive data |
| Storage | base64 encoded (not encrypted by default) | Plain text |
| In this project | `MONGO_INITDB_ROOT_USERNAME/PASSWORD` | `ME_CONFIG_MONGODB_SERVER` (service URL) |

> Rule: sensitive data → Secret. Everything else → ConfigMap.

---

## Base64 Encoding (how secret values are made)

```powershell
# Encode
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("mongouser"))
# Output: bW9uZ291c2Vy

[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("mongopass"))
# Output: bW9uZ29wYXNz

# Decode (verify)
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("bW9uZ291c2Vy"))
# Output: mongouser
```

---

## How Pods Connect (the wiring)

```
Browser
  │
  │ nodePort: 30000
  ▼
mongoexpress-service (LoadBalancer)
  │
  │ port: 8081
  ▼
mongo-express Pod
  │
  │ ME_CONFIG_MONGODB_SERVER = "mongodb-service"  ← from ConfigMap
  │ ME_CONFIG_MONGODB_ADMINUSERNAME               ← from Secret
  │ ME_CONFIG_MONGODB_ADMINPASSWORD               ← from Secret
  ▼
mongodb-service (ClusterIP)        ← K8s DNS resolves "mongodb-service" to this
  │
  │ port: 27017
  ▼
mongodb Pod
  │
  │ MONGO_INITDB_ROOT_USERNAME     ← from Secret
  │ MONGO_INITDB_ROOT_PASSWORD     ← from Secret
```

**Key concept:** Pods don't talk to each other directly. Mongo Express reaches MongoDB via the Service name `mongodb-service`. Kubernetes DNS resolves that name to the ClusterIP automatically.

---

## Apply Order (order matters)

```powershell
# 1. Secret first — Deployment will crash if secret missing at pod start
kubectl apply -f mongo-secret.yaml

# 2. ConfigMap before mongo-express — same reason
kubectl apply -f mongo-configmap.yaml

# 3. MongoDB pod
kubectl apply -f mongo-deployment.yaml

# 4. MongoDB internal service — exposes MongoDB to cluster
kubectl apply -f mongo-service.yaml

# 5. Mongo Express pod + external service
kubectl apply -f mongoexpress-deployment.yaml
```

---

## Verify Everything Running

```powershell
# Check pods status (want: Running)
kubectl get pods

# Check all services
kubectl get services

# Describe a pod (debug crashes)
kubectl describe pod <pod-name>

# Check pod logs
kubectl logs <pod-name>

# Check if secret exists
kubectl get secret mongodb-secret

# Check if configmap exists
kubectl get configmap mongodb-configmap
```

---

## Access Mongo Express UI

```powershell
# Minikube (local)
minikube service mongoexpress-service

# Or manually — get node IP
minikube ip
# Then open: http://<minikube-ip>:30000
```

### Windows + Docker Driver (Important)

On Windows with Docker driver, `192.168.49.2` is not directly reachable. Minikube creates a tunnel instead:

```
┌──────────────────────┬─────────────┬────────────────────────┐
│         NAME         │ TARGET PORT │          URL           │
├──────────────────────┼─────────────┼────────────────────────┤
│ mongoexpress-service │             │ http://127.0.0.1:XXXXX │
└──────────────────────┴─────────────┴────────────────────────┘
```

- Use `http://127.0.0.1:XXXXX` (tunnel URL) — **not** the `192.168.49.2` URL
- Terminal running `minikube service` must stay open — closing it kills the tunnel
- Browser opens automatically

### Browser Login Popup (Basic Auth)

Mongo Express shows a browser login prompt. These are **UI credentials**, not MongoDB credentials:

| Field | Value |
|-------|-------|
| Username | `admin` |
| Password | `pass` |

> Do not use `mongouser`/`mongopass` here — those are MongoDB internal credentials.

---

## Service Types Explained

| Type | Accessible from | Used for |
|------|----------------|----------|
| `ClusterIP` (default) | Inside cluster only | MongoDB — no external access needed |
| `LoadBalancer` | External (browser) | Mongo Express — needs browser access |
| `NodePort` | External via node IP + port | Same as LoadBalancer on local/minikube |

> MongoDB uses ClusterIP because only Mongo Express needs to reach it — no reason to expose DB externally.

---

## Teardown

```powershell
kubectl delete -f mongoexpress-deployment.yaml
kubectl delete -f mongo-service.yaml
kubectl delete -f mongo-deployment.yaml
kubectl delete -f mongo-configmap.yaml
kubectl delete -f mongo-secret.yaml
```

> Delete in reverse order — remove consumers before providers.
