# Example 3 — YAML Components Explained

Deep dive into every file. Every key, every value, why it exists.

---

## 1. `01-my-app-deployment.yaml` — App Deployment + ConfigMap

### Deployment section

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: my-app
  labels:
    app: my-app
spec:
  replicas: 2
  selector:
    matchLabels:
      app: my-app
  template:
    metadata:
      labels:
        app: my-app
    spec:
      containers:
        - name: my-app
          image: nginx:1.25
          ports:
            - containerPort: 80
          volumeMounts:
            - name: html
              mountPath: /usr/share/nginx/html
      volumes:
        - name: html
          configMap:
            name: my-app-html
```

| Field | Value | Purpose |
|-------|-------|---------|
| `apiVersion` | `apps/v1` | Deployment belongs to `apps` API group |
| `kind` | `Deployment` | Manages ReplicaSet → manages Pods |
| `replicas` | `2` | Two pods for my-app. Service load balances between them |
| `selector.matchLabels` | `app: my-app` | Deployment controls pods with this label |
| `template.metadata.labels` | `app: my-app` | Pods get this label — must match selector |
| `image` | `nginx:1.25` | Nginx container. Serves HTML from /usr/share/nginx/html |
| `containerPort` | `80` | Port nginx listens on inside the container |
| `volumeMounts.mountPath` | `/usr/share/nginx/html` | Replaces nginx's default HTML with our ConfigMap content |
| `volumes.configMap.name` | `my-app-html` | Mounts the ConfigMap below as a volume |

### ConfigMap section

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: my-app-html
data:
  index.html: |
    <html>...</html>
```

| Field | Purpose |
|-------|---------|
| `kind: ConfigMap` | Stores non-secret config data as key-value pairs |
| `data.index.html` | Key = filename. Value = file content. Mounted at `/usr/share/nginx/html/index.html` |
| `\|` (pipe) | YAML block scalar — preserves multiline string with newlines |

**Why ConfigMap for HTML?**  
Baking HTML into the image would need a rebuild on every change.  
ConfigMap injects content at runtime without touching the image.

---

## 2. `02-my-app-service.yaml` — ClusterIP Service

```yaml
apiVersion: v1
kind: Service
metadata:
  name: my-app-service
spec:
  selector:
    app: my-app
  ports:
    - protocol: TCP
      port: 80
      targetPort: 80
```

| Field | Value | Purpose |
|-------|-------|---------|
| `kind: Service` | `Service` | Stable internal endpoint for a set of pods |
| `selector.app` | `my-app` | Routes traffic to pods labelled `app: my-app` |
| `port` | `80` | Port exposed inside the cluster (what Ingress targets) |
| `targetPort` | `80` | Port on the pod container (nginx listens here) |
| No `type` field | defaults to `ClusterIP` | Internal only — intentional. Ingress handles external access |

**Why ClusterIP and not NodePort/LoadBalancer?**  
The Ingress Controller is already the external entry point. Services only need to be reachable internally. Using ClusterIP keeps services private and lets Ingress be the single controlled gateway.

---

## 3. `03-my-api-deployment.yaml` — API Deployment + ConfigMap

Same structure as `01-my-app-deployment.yaml`. Key differences:

| Field | my-app | my-api |
|-------|--------|--------|
| `metadata.name` | `my-app` | `my-api` |
| `replicas` | `2` | `1` |
| `selector/labels` | `app: my-app` | `app: my-api` |
| `configMap.name` | `my-app-html` | `my-api-html` |
| HTML content | Green background | Blue background |

Two separate Deployments = two independently scalable, independently deployable workloads.  
They share nothing except the same Ingress entry point.

---

## 4. `04-my-api-service.yaml` — ClusterIP Service for API

```yaml
apiVersion: v1
kind: Service
metadata:
  name: my-api-service
spec:
  selector:
    app: my-api
  ports:
    - protocol: TCP
      port: 80
      targetPort: 80
```

Identical structure to `my-app-service`. Selector targets `app: my-api` pods only.  
Service name `my-api-service` is what the Ingress resource references.

---

## 5. `05-my-app-ingress.yaml` — The Ingress Resource

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: my-app-ingress
  annotations:
    nginx.ingress.kubernetes.io/rewrite-target: /
spec:
  ingressClassName: nginx
  rules:
    - host: myapp.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: my-app-service
                port:
                  number: 80
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: my-api-service
                port:
                  number: 80
```

### Field breakdown

| Field | Value | Purpose |
|-------|-------|---------|
| `apiVersion` | `networking.k8s.io/v1` | Stable Ingress API (v1beta1 is deprecated — don't use it) |
| `kind` | `Ingress` | Routing rules object. Not a pod. Just config. |
| `metadata.name` | `my-app-ingress` | Identifies this rule set. One cluster can have many Ingress objects |

### Annotations

```yaml
annotations:
  nginx.ingress.kubernetes.io/rewrite-target: /
```

| Annotation | Purpose |
|------------|---------|
| `rewrite-target: /` | Strips the matched path prefix before forwarding. When request comes in at `/api/something`, the pod sees `/something`, not `/api/something`. Without this, nginx in the pod returns 404 (it doesn't have an `/api` route). |

### Spec fields

| Field | Value | Purpose |
|-------|-------|---------|
| `ingressClassName` | `nginx` | Tells K8s which Ingress Controller handles this resource. minikube's built-in controller uses class `nginx` |
| `rules[].host` | `myapp.com` | Only match requests with this Host header. Requests to other hosts are not routed |
| `rules[].http.paths` | array | List of path → backend mappings |
| `path: /` | `/` | Matches any request starting with `/` |
| `path: /api` | `/api` | Matches requests starting with `/api` — more specific wins |
| `pathType: Prefix` | `Prefix` | Prefix match. `/api` matches `/api`, `/api/users`, `/api/v2/items` |
| `backend.service.name` | service name | Which Service to forward to |
| `backend.service.port.number` | `80` | Which port on that Service |

### Path matching priority

When multiple paths could match, **longest / most specific path wins**.

```
Request: myapp.com/api/users
  Candidates: /  and  /api
  Winner: /api  (longer prefix match)
  Forwarded to: my-api-service
```

```
Request: myapp.com/home
  Candidates: /  and  /api
  Winner: /  (only match)
  Forwarded to: my-app-service
```

### What `ingressClassName: nginx` connects

```
Ingress resource                    Ingress Controller
─────────────────                   ─────────────────
ingressClassName: nginx  ─────────► ingress-nginx pod
                                    (watches for Ingress resources
                                     with class=nginx, loads their
                                     rules into its nginx.conf)
```

Without `ingressClassName`, the controller may ignore your Ingress resource.

---

## Key Relationships Summary

```
ConfigMap (my-app-html)
    │  mounted as volume
    ▼
Deployment (my-app)  ──creates──►  Pod (app=my-app label)
                                        │
Service (my-app-service)  ◄─────────────┘
    selector: app=my-app              found by label

Ingress (my-app-ingress)
    path: /  → my-app-service:80
    path: /api → my-api-service:80
         │
         │  reads rules
         ▼
Ingress Controller Pod (nginx, in ingress-nginx namespace)
    actually routes the traffic
```

---

## apiVersion Reference

| Resource | apiVersion |
|----------|------------|
| Deployment | `apps/v1` |
| Service | `v1` |
| ConfigMap | `v1` |
| Ingress | `networking.k8s.io/v1` |

Core resources (Pod, Service, ConfigMap, Namespace) use plain `v1`.  
Extended resources (Deployment, ReplicaSet) use `apps/v1`.  
Network-layer resources (Ingress, NetworkPolicy) use `networking.k8s.io/v1`.
