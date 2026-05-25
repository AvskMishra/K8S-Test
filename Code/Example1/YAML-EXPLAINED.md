# Kubernetes YAML — Every Field Explained

Deep dive into each YAML file. Every key, every value, why it exists.

---

## 1. `mongo-deployment.yaml` — MongoDB Pod

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: mongodb-deployment
  labels:
    app: mongodb
spec:
  replicas: 1
  selector:
    matchLabels:
      app: mongodb
  template:
    metadata:
      labels:
        app: mongodb
    spec:
      containers:
        - name: mongodb
          image: mongo:6.0
          ports:
            - containerPort: 27017
          env:
            - name: MONGO_INITDB_ROOT_USERNAME
              valueFrom:
                secretKeyRef:
                  name: mongodb-secret
                  key: mongo-root-username
            - name: MONGO_INITDB_ROOT_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: mongodb-secret
                  key: mongo-root-password
```

### Field-by-Field Breakdown

---

#### `apiVersion: apps/v1`

Tells Kubernetes **which API group and version** to use for this resource.

- `apps` = the API group that handles workload resources (Deployments, StatefulSets, DaemonSets)
- `v1` = stable version of that group
- Deployment lives under `apps/v1` — not plain `v1` (which is for core resources like Pods, Services, Secrets)

> If you wrote `apiVersion: v1` for a Deployment, kubectl would reject it — wrong API group.

---

#### `kind: Deployment`

Declares **what type of Kubernetes object** this file creates.

A `Deployment` does three things:
1. Creates Pods (via the template below)
2. Ensures the desired number of Pods are always running (self-healing)
3. Manages rolling updates — replace old pods with new ones without downtime

Other kinds you'll see: `Pod`, `Service`, `Secret`, `ConfigMap`, `StatefulSet`, `DaemonSet`.

> You never create a Pod directly in production. A Deployment manages Pods so if one crashes, Kubernetes recreates it automatically.

---

#### `metadata:`

Top-level info about the Deployment object itself (not about the pods it creates).

```yaml
metadata:
  name: mongodb-deployment
  labels:
    app: mongodb
```

- **`name: mongodb-deployment`** — unique name for this Deployment inside the namespace. `kubectl get deployments` shows this name. `kubectl delete deployment mongodb-deployment` uses this name.
- **`labels:`** — key-value tags attached to this Deployment object. Used for organization and selection. `app: mongodb` is a convention — you can have multiple labels like `env: production`, `tier: database`, etc.

> Labels on the Deployment metadata are for **humans and tooling** (dashboards, `kubectl get deployment -l app=mongodb`). They don't affect pod routing.

---

#### `spec:`

The **desired state** of this Deployment. Everything under `spec` tells Kubernetes what you want.

---

#### `spec.replicas: 1`

How many identical Pod copies to run simultaneously.

- `replicas: 1` → 1 MongoDB pod running
- `replicas: 3` → 3 identical pods running (for HA or load distribution)
- If a pod crashes, the Deployment controller notices actual replicas (0) ≠ desired (1) and creates a new pod

> For stateful databases, `replicas: 1` is standard unless you configure proper MongoDB replication with persistent volumes. Multiple DB replicas without shared storage = data split across pods.

---

#### `spec.selector:`

Defines **which Pods this Deployment manages**.

```yaml
selector:
  matchLabels:
    app: mongodb
```

- The Deployment finds and owns Pods that have the label `app: mongodb`
- If you manually created a Pod with `app: mongodb`, this Deployment would adopt it
- **Must match** `spec.template.metadata.labels` below — if they don't match, kubectl rejects the file

> The selector is the link between the Deployment controller and the Pods it manages. Without it, the Deployment wouldn't know which pods to count, heal, or update.

---

#### `spec.template:`

The **blueprint for every Pod** this Deployment creates. Every replica is built from this template.

```yaml
template:
  metadata:
    labels:
      app: mongodb
  spec:
    containers: [...]
```

- `template.metadata.labels` — labels stamped onto every Pod created. **Must match `spec.selector.matchLabels`.**
- These labels are also what the Service uses to find pods (via its own selector)

> Think of `template` as the Pod spec wrapped inside the Deployment. The Deployment reads it to know what container image, env vars, ports, and volumes each Pod needs.

---

#### `spec.template.spec.containers:`

List of containers to run inside each Pod.

> A Pod can run multiple containers (sidecar pattern). Here we have one: MongoDB.

---

#### `containers[0].name: mongodb`

Internal name for this container within the Pod.

- Used in `kubectl logs <pod-name> -c mongodb` when pod has multiple containers
- Used in `kubectl exec -it <pod-name> -c mongodb -- bash`
- No external significance — just a label inside the Pod spec

---

#### `containers[0].image: mongo:6.0`

Docker image to run. Kubernetes pulls this from Docker Hub (by default).

- `mongo` = image name on Docker Hub
- `6.0` = specific version tag
- Without a tag (just `mongo`) it pulls `latest` — **avoid in production**, `latest` can change unexpectedly
- Image pull happens on the node where the Pod is scheduled

> Pinning to `6.0` means a `kubectl apply` six months from now still gets the same MongoDB version, not whatever "latest" is then.

---

#### `containers[0].ports:`

```yaml
ports:
  - containerPort: 27017
```

Declares that this container **listens on port 27017** inside the Pod.

- `containerPort` is **documentation only** — it doesn't actually open or expose the port
- MongoDB's default port is `27017`
- The actual network exposure happens in the Service, not here

> Removing `containerPort` from the YAML would still work — traffic would still flow. It exists so humans and tools know what port the app uses.

---

#### `containers[0].env:`

List of environment variables injected into the container at startup.

MongoDB's official Docker image reads two specific env vars to create the root admin user:
- `MONGO_INITDB_ROOT_USERNAME` → admin username
- `MONGO_INITDB_ROOT_PASSWORD` → admin password

These are set on **first start** when MongoDB initializes its data directory. If the DB is already initialized (persistent volume with data), these vars are ignored.

---

#### `valueFrom.secretKeyRef:`

```yaml
valueFrom:
  secretKeyRef:
    name: mongodb-secret
    key: mongo-root-username
```

Instead of hardcoding the value, pull it from a Kubernetes Secret.

- `name: mongodb-secret` — name of the Secret object (must exist in same namespace before pod starts)
- `key: mongo-root-username` — which key inside that Secret to use

Kubernetes decodes the base64 value from the Secret and injects it as a plain string into the container's environment. The container sees `MONGO_INITDB_ROOT_USERNAME=mongouser`, not the base64 version.

> If the Secret doesn't exist when the Pod starts, the Pod enters `CreateContainerConfigError` state and won't run. This is why Secret must be applied before the Deployment.

---
---

## 2. `mongo-secret.yaml` — Credentials Storage

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: mongodb-secret
type: Opaque
data:
  mongo-root-username: bW9uZ291c2Vy
  mongo-root-password: bW9uZ29wYXNz
```

### Field-by-Field Breakdown

---

#### `apiVersion: v1`

Secret is a **core Kubernetes resource** — lives in the `v1` API (no group prefix like `apps/v1`).

Core resources (`v1`): Pod, Service, Secret, ConfigMap, Namespace, Node, PersistentVolume.

---

#### `kind: Secret`

Kubernetes object for storing sensitive data.

Differences from ConfigMap:
- Stored in etcd with base64 encoding (not encryption by default, but can be encrypted at rest with additional cluster config)
- Access can be restricted via RBAC more granularly
- Shows as `<secret>` in some kubectl outputs
- Designed to be referenced by Pods without embedding values in Deployment YAML

---

#### `metadata.name: mongodb-secret`

Name used to reference this Secret from other resources.

In `mongo-deployment.yaml`:
```yaml
secretKeyRef:
  name: mongodb-secret   # ← must match exactly
```

Same namespace requirement: Secret and the Pod consuming it must be in the same namespace.

---

#### `type: Opaque`

Tells Kubernetes the format/schema of this Secret's data.

| Type | Purpose |
|------|---------|
| `Opaque` | Generic, arbitrary key-value data — most common |
| `kubernetes.io/dockerconfigjson` | Docker registry credentials |
| `kubernetes.io/tls` | TLS certificate + private key |
| `kubernetes.io/service-account-token` | Service account tokens |

`Opaque` = "I'll handle the structure myself, no validation needed."

---

#### `data:`

Key-value pairs where **values must be base64 encoded**.

```yaml
data:
  mongo-root-username: bW9uZ291c2Vy   # base64 of "mongouser"
  mongo-root-password: bW9uZ29wYXNz   # base64 of "mongopass"
```

**Why base64?** Secrets can contain binary data (certificates, keys) that can't be embedded directly in YAML as text. Base64 makes any binary data safe for YAML. It is **not encryption** — anyone with cluster access can decode it instantly.

**Encode yourself (PowerShell):**
```powershell
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("mongouser"))
# bW9uZ291c2Vy
```

**Decode to verify:**
```powershell
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("bW9uZ291c2Vy"))
# mongouser
```

> Alternative: use `stringData:` instead of `data:` — then you write plain text and Kubernetes encodes it for you. Useful in dev, but `data:` is more explicit.

---
---

## 3. `mongo-configmap.yaml` — Non-Sensitive Configuration

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: mongodb-configmap
data:
  database_url: mongodb-service
```

### Field-by-Field Breakdown

---

#### `kind: ConfigMap`

Stores **non-sensitive configuration** as plain text key-value pairs.

Use ConfigMap for anything you'd put in a config file or environment variable that isn't a secret: URLs, feature flags, port numbers, file contents, config files.

---

#### `metadata.name: mongodb-configmap`

Referenced in `mongoexpress-deployment.yaml`:
```yaml
configMapKeyRef:
  name: mongodb-configmap   # ← must match
  key: database_url
```

---

#### `data.database_url: mongodb-service`

Plain text value — no encoding needed.

Value `mongodb-service` is the **Kubernetes Service name** for MongoDB (defined in `mongo-service.yaml`).

**Why does this work as a hostname?**

Kubernetes has built-in DNS. Every Service gets a DNS record: `<service-name>.<namespace>.svc.cluster.local`. Within the same namespace, you can use just the Service name (`mongodb-service`) and DNS resolves it to the ClusterIP automatically.

When Mongo Express starts, it reads `ME_CONFIG_MONGODB_SERVER=mongodb-service`, then connects to `mongodb-service:27017`. Kubernetes DNS resolves `mongodb-service` → ClusterIP → MongoDB pod.

> If you hardcoded the ClusterIP (e.g., `10.96.45.123`) instead of the service name, the IP could change when the service is recreated. Service name DNS is stable.

---
---

## 4. `mongo-service.yaml` — Internal Network Access

```yaml
apiVersion: v1
kind: Service
metadata:
  name: mongodb-service
spec:
  selector:
    app: mongodb
  ports:
    - protocol: TCP
      port: 27017
      targetPort: 27017
```

### Field-by-Field Breakdown

---

#### `kind: Service`

Provides **stable network access** to a set of Pods.

Problem without Services:
- Pods get random IP addresses
- Pod IP changes every time it restarts
- Other pods can't reliably find it

Service solution:
- Stable IP (ClusterIP) and DNS name
- Automatically routes to healthy pods matching its selector
- Acts as load balancer if multiple replicas

---

#### `metadata.name: mongodb-service`

This becomes the **DNS hostname** for reaching MongoDB inside the cluster.

Any pod in the same namespace can reach MongoDB at:
- `mongodb-service` (short form, same namespace)
- `mongodb-service.default.svc.cluster.local` (fully qualified)

This exact name is stored in the ConfigMap (`database_url: mongodb-service`) so Mongo Express can find it.

---

#### `spec.selector:`

```yaml
selector:
  app: mongodb
```

Service finds its target Pods by matching this label selector.

Pods with label `app: mongodb` = MongoDB pods (defined in `mongo-deployment.yaml` → `template.metadata.labels`).

When traffic arrives at the Service, it forwards to any healthy Pod matching this selector. If there were 3 MongoDB replicas, the Service would round-robin across all 3.

---

#### `spec.ports:`

```yaml
ports:
  - protocol: TCP
    port: 27017
    targetPort: 27017
```

| Field | Meaning |
|-------|---------|
| `protocol: TCP` | Network protocol. MongoDB uses TCP. Other option: `UDP`. |
| `port: 27017` | Port the **Service** listens on — what other pods connect to |
| `targetPort: 27017` | Port the **container** listens on — where traffic gets forwarded |

Here both are 27017 — Service port matches container port. They can differ:
- `port: 80` + `targetPort: 3000` → external clients connect to port 80, container gets traffic on 3000

---

#### No `type:` field

When `type` is omitted, it defaults to **`ClusterIP`**.

`ClusterIP` = only reachable from within the cluster. No external access. Perfect for MongoDB — only Mongo Express (inside cluster) needs to reach it. No reason to expose the database externally.

---
---

## 5. `mongoexpress-deployment.yaml` — UI Pod + External Service

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: mongoexpress-deployment
  labels:
    app: mongoexpress
spec:
  replicas: 1
  selector:
    matchLabels:
      app: mongoexpress
  template:
    metadata:
      labels:
        app: mongoexpress
    spec:
      containers:
        - name: mongoexpress
          image: mongo-express:1.0.2
          ports:
            - containerPort: 8081
          env:
            - name: ME_CONFIG_MONGODB_ADMINUSERNAME
              valueFrom:
                secretKeyRef:
                  name: mongodb-secret
                  key: mongo-root-username
            - name: ME_CONFIG_MONGODB_ADMINPASSWORD
              valueFrom:
                secretKeyRef:
                  name: mongodb-secret
                  key: mongo-root-password
            - name: ME_CONFIG_MONGODB_SERVER
              valueFrom:
                configMapKeyRef:
                  name: mongodb-configmap
                  key: database_url
---
apiVersion: v1
kind: Service
metadata:
  name: mongoexpress-service
spec:
  selector:
    app: mongoexpress
  type: LoadBalancer
  ports:
    - protocol: TCP
      port: 8081
      targetPort: 8081
      nodePort: 30000
```

### Field-by-Field Breakdown

---

#### Deployment section (lines 1–37)

Same structure as `mongo-deployment.yaml`. Key differences:

**`image: mongo-express:1.0.2`**
Mongo Express = web-based MongoDB admin UI. Runs on port 8081. Pinned to version `1.0.2`.

**`containerPort: 8081`**
Mongo Express web UI port. Browser traffic lands here after passing through the Service.

---

#### `ME_CONFIG_MONGODB_ADMINUSERNAME` / `ME_CONFIG_MONGODB_ADMINPASSWORD`

Mongo Express needs credentials to log into MongoDB.

Uses the **same Secret** (`mongodb-secret`) with the same keys as the MongoDB Deployment. Both pods read from the same source of truth — if you change the Secret, both pods get updated credentials.

```yaml
valueFrom:
  secretKeyRef:
    name: mongodb-secret
    key: mongo-root-username
```

---

#### `ME_CONFIG_MONGODB_SERVER`

```yaml
- name: ME_CONFIG_MONGODB_SERVER
  valueFrom:
    configMapKeyRef:
      name: mongodb-configmap
      key: database_url
```

Tells Mongo Express **where to find MongoDB**.

- Source: ConfigMap (not Secret) — service URLs are not sensitive
- `configMapKeyRef` works identically to `secretKeyRef` but reads from a ConfigMap
- Value injected: `mongodb-service` (the DNS name of the MongoDB Service)
- Mongo Express connects to `mongodb-service:27017` at startup

> This is why apply order matters: ConfigMap and Secret must exist before Mongo Express pod starts. Pod reads these at container creation — missing reference = pod crash.

---

#### `---` (document separator)

YAML allows multiple documents in one file separated by `---`.

This single file defines two Kubernetes objects:
1. A Deployment (Mongo Express pod)
2. A Service (external access)

You can split them into separate files — combining is just convenience. `kubectl apply -f mongoexpress-deployment.yaml` applies both objects.

---

#### Service section — `type: LoadBalancer`

```yaml
spec:
  selector:
    app: mongoexpress
  type: LoadBalancer
  ports:
    - protocol: TCP
      port: 8081
      targetPort: 8081
      nodePort: 30000
```

**`type: LoadBalancer`**

Exposes the Service **externally** — accessible from outside the cluster.

| Behavior | Cloud (AWS/GCP/Azure) | Minikube (local) |
|----------|----------------------|------------------|
| What happens | Cloud provider provisions a real load balancer with external IP | Minikube simulates it — `minikube service mongoexpress-service` opens browser |
| External IP | Assigned automatically | Use `minikube ip` + nodePort |

`LoadBalancer` implicitly includes `NodePort` and `ClusterIP` — it's a superset. Traffic path: external IP → NodePort on node → ClusterIP → Pod.

---

#### `nodePort: 30000`

The port opened on **every cluster node's IP** for external access.

- Range: 30000–32767 (Kubernetes reserved range for NodePorts)
- Any node IP + port 30000 reaches this Service
- On Minikube: `http://<minikube-ip>:30000` opens Mongo Express

If you omit `nodePort`, Kubernetes assigns a random port in the 30000–32767 range.

---

#### Port flow summary for this Service

```
Browser request → port 30000 (nodePort on node)
  → port 8081 (Service ClusterIP port)
    → port 8081 (containerPort on Mongo Express pod)
```

All three are 8081 here (except nodePort 30000 for external entry).

---
---

## How All Files Connect

```
mongo-secret.yaml
  └─ provides credentials to:
       ├─ mongo-deployment.yaml  (MONGO_INITDB_ROOT_USERNAME/PASSWORD)
       └─ mongoexpress-deployment.yaml (ME_CONFIG_MONGODB_ADMINUSERNAME/PASSWORD)

mongo-configmap.yaml
  └─ provides service URL to:
       └─ mongoexpress-deployment.yaml (ME_CONFIG_MONGODB_SERVER = "mongodb-service")

mongo-deployment.yaml
  └─ creates Pod with label: app=mongodb
       └─ mongo-service.yaml selects it (selector: app: mongodb)
            └─ DNS: mongodb-service → ClusterIP → MongoDB Pod

mongoexpress-deployment.yaml
  └─ creates Pod with label: app=mongoexpress
       └─ mongoexpress-service selects it (selector: app: mongoexpress)
            └─ external access: nodePort 30000 → port 8081 → Mongo Express Pod
                 └─ Mongo Express connects to "mongodb-service:27017" internally
```

---

## Key Concepts Recap

| Concept | What it does | Example in project |
|---------|-------------|-------------------|
| `apiVersion` | Which Kubernetes API handles this resource | `apps/v1` for Deployment, `v1` for Service/Secret/ConfigMap |
| `kind` | Type of object to create | `Deployment`, `Service`, `Secret`, `ConfigMap` |
| `metadata.name` | Unique identifier — used in selectors and references | `mongodb-secret`, `mongodb-service` |
| `labels` | Tags for selection and organization | `app: mongodb` |
| `selector.matchLabels` | Links Deployment to its Pods | Must match `template.metadata.labels` |
| `replicas` | How many Pod copies to run | `1` for both pods here |
| `containerPort` | Documents container's listen port | `27017` (MongoDB), `8081` (Mongo Express) |
| `secretKeyRef` | Pull env var value from a Secret | Credentials for both pods |
| `configMapKeyRef` | Pull env var value from a ConfigMap | MongoDB service URL for Mongo Express |
| `ClusterIP` (default) | Internal-only Service | MongoDB — no external access needed |
| `LoadBalancer` | External Service | Mongo Express — browser needs to reach it |
| `nodePort` | External port on node IP | `30000` — browser connects here |
| `targetPort` | Container port traffic is forwarded to | Matches `containerPort` |
