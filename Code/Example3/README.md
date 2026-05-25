# Example 3 — Kubernetes Ingress

## The Problem: Why Ingress Exists

Without Ingress, exposing apps to the internet means creating a **LoadBalancer Service** per app.

```
App 1 ──► LoadBalancer (cloud IP #1, costs money)
App 2 ──► LoadBalancer (cloud IP #2, costs money)
App 3 ──► LoadBalancer (cloud IP #3, costs money)
```

Every LoadBalancer = a separate cloud IP address = separate billing.  
No URL routing. No TLS termination in one place. No central control.

---

## The Solution: One Ingress Controller, Many Apps

Ingress gives you **one entry point** that routes to many services based on host or URL path.

```
                         myapp.com/       ──► my-app-service ──► my-app pods
External Request ──►  [ Ingress Controller ]
                         myapp.com/api    ──► my-api-service  ──► my-api pods
```

One cloud LoadBalancer, one IP, unlimited routing rules.

---

## Architecture: Component Roles

```
┌─────────────────────────────────────────────────────────────────────┐
│                         K8s Cluster                                  │
│                                                                      │
│  Browser ──► (minikube IP:80)                                        │
│                    │                                                 │
│                    ▼                                                 │
│     ┌──────────────────────────────┐                                 │
│     │   Ingress Controller Pod     │  ← nginx pod in ingress-nginx   │
│     │   (nginx inside a pod)       │    namespace. Does actual       │
│     │                              │    HTTP routing work.           │
│     └──────────────┬───────────────┘                                 │
│                    │  reads rules from ↓                             │
│     ┌──────────────▼───────────────┐                                 │
│     │   my-app-ingress             │  ← Ingress resource. Just a    │
│     │   (Ingress resource/rules)   │    config object. Defines who  │
│     │   host: myapp.com            │    goes where. No traffic      │
│     │   /     → my-app-service     │    flows through this object   │
│     │   /api  → my-api-service     │    itself.                     │
│     └──────┬───────────────┬───────┘                                 │
│            │               │                                         │
│            ▼               ▼                                         │
│   ┌──────────────┐  ┌──────────────┐                                 │
│   │my-app-service│  │my-api-service│  ← ClusterIP services. No      │
│   │  (port 80)   │  │  (port 80)   │    external access. Internal   │
│   └──────┬───────┘  └──────┬───────┘    load balance to pods.       │
│          │                 │                                         │
│          ▼                 ▼                                         │
│   ┌────────────┐    ┌────────────┐                                   │
│   │ my-app pod │    │ my-api pod │  ← Pods running nginx with       │
│   │ my-app pod │    └────────────┘    custom HTML via ConfigMap.     │
│   └────────────┘                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Component Roles — Explained

### 1. Ingress Controller Pod
- **What**: A real running pod (nginx) inside the `ingress-nginx` namespace.
- **Role**: Receives all incoming HTTP/HTTPS traffic. Reads Ingress rules. Routes requests to the correct Service.
- **Enabled via**: `minikube addons enable ingress`
- **Without this**: Ingress resources do nothing. The resource is just ignored config.

### 2. Ingress Resource (`my-app-ingress`)
- **What**: A Kubernetes config object (like a routing table).
- **Role**: Declares the rules — "host X + path Y → service Z". No traffic passes through this object itself.
- **Think of it as**: nginx.conf equivalent, but managed by kubectl.
- **Key fields**: `host`, `paths`, `pathType`, `backend.service`

### 3. Service (`my-app-service`, `my-api-service`)
- **What**: ClusterIP Service (internal only, no NodePort or LoadBalancer needed).
- **Role**: Stable internal DNS name for the pods. Load balances across all matching pods.
- **Why ClusterIP**: Ingress controller talks to services internally — no need for external exposure.

### 4. Pod (`my-app`, `my-api`)
- **What**: Containers running nginx, serving HTML injected via ConfigMap.
- **Role**: Actual workload. Responds to HTTP requests.
- **my-app**: 2 replicas — the service load balances between them.
- **my-api**: 1 replica — separate backend, different response.

---

## Request Flow — Step by Step

```
Step 1: Browser hits myapp.com/api
        ↓
Step 2: DNS resolves myapp.com → minikube IP  (set via /etc/hosts)
        ↓
Step 3: Ingress Controller Pod receives request on port 80
        ↓
Step 4: Controller checks loaded Ingress rules (from my-app-ingress)
        Path /api matches → route to my-api-service:80
        ↓
Step 5: my-api-service receives request
        Selects a matching pod (app=my-api)
        ↓
Step 6: my-api pod responds with HTML
        ↓
Step 7: Response travels back through controller → browser
```

---

## Why Two Apps in This Example?

One app + Ingress = underwhelming.  
Two apps + **one Ingress** = the actual value proposition.

```
myapp.com/         → my-app-service  (green page: "my-app is alive!")
myapp.com/api      → my-api-service  (blue page:  "my-api is alive!")
```

Both served through **one controller pod**, **one IP**, **zero extra LoadBalancers**.

---

## Default Backend

When a request matches no rule (e.g., `myapp.com/unknown`), the Ingress Controller returns a 404.  
You can configure a **default backend** service to handle these unmatched requests gracefully.

```bash
kubectl describe ingress my-app-ingress
# Look for: Default backend: <default>  (no custom one set here)
```

---

## Setup & Run

See [COMMANDS.md](COMMANDS.md) for the full step-by-step run sequence.
