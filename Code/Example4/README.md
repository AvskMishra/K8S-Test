# Example 4 — Helm: Kubernetes Package Manager

## Where Helm Fits in the K8s Story

```
Example 1 → Raw YAML files (manual kubectl apply)
Example 2 → Namespaces (organize resources)
Example 3 → Ingress (smart routing)
Example 4 → Helm (package, template, and manage all of the above)
```

Without Helm, deploying an app = applying 5–10 YAML files manually, in order, every time.  
Change one value (image tag) = edit multiple files by hand.  
Roll back = `kubectl delete` + reapply old files.

**Helm solves this.** One command installs. One command rolls back. One file controls all variables.

---

## What Is Helm?

Helm is the **package manager for Kubernetes** — like apt for Ubuntu, or npm for Node.

| Concept       | npm equivalent   | Helm equivalent         |
|---------------|------------------|-------------------------|
| Package       | npm package      | Helm Chart              |
| Registry      | npmjs.com        | Artifact Hub / OCI repo |
| Install       | `npm install`    | `helm install`          |
| Installed pkg | node_modules/pkg | Helm Release            |
| Config file   | package.json     | values.yaml             |

---

## Helm Chart Structure

```
my-app/                        ← Chart root directory (the chart name)
├── Chart.yaml                 ← Chart metadata (name, version, description)
├── values.yaml                ← Default configuration values
├── charts/                    ← Sub-charts / dependencies
├── templates/                 ← Kubernetes YAML templates (Go template syntax)
│   ├── deployment.yaml
│   ├── service.yaml
│   ├── ingress.yaml
│   ├── configmap.yaml
│   ├── _helpers.tpl           ← Reusable template snippets (not rendered directly)
│   ├── NOTES.txt              ← Post-install instructions shown to user
│   └── tests/
│       └── test-connection.yaml
└── .helmignore                ← Files to ignore (like .gitignore)
```

### Chart.yaml

```yaml
apiVersion: v2                 # Helm 3 uses v2, Helm 2 used v1
name: my-app
description: A sample Node.js app chart
type: application              # application or library
version: 1.2.0                 # chart version (SemVer)
appVersion: "3.0.1"            # version of the app inside the chart
dependencies:
  - name: mongodb
    version: "13.x.x"
    repository: "https://charts.bitnami.com/bitnami"
```

### values.yaml (Default Values)

```yaml
replicaCount: 2

image:
  repository: myregistry.io/my-app
  tag: "3.0.1"
  pullPolicy: IfNotPresent

service:
  type: ClusterIP
  port: 80

ingress:
  enabled: true
  host: myapp.example.com

resources:
  limits:
    cpu: 500m
    memory: 256Mi
  requests:
    cpu: 250m
    memory: 128Mi

env:
  NODE_ENV: production
  LOG_LEVEL: info
```

---

## Helm Templating Engine

Helm uses **Go's `text/template`** engine. Templates are regular Kubernetes YAML files with `{{ }}` placeholders injected.

### How It Works

```
values.yaml  ──┐
               ├──► Helm Template Engine ──► Final rendered YAML ──► kubectl apply
templates/   ──┘
```

### Template Syntax

| Syntax                         | Meaning                                        |
|--------------------------------|------------------------------------------------|
| `{{ .Values.image.tag }}`      | Read a value from values.yaml                  |
| `{{ .Release.Name }}`          | The release name given at install time         |
| `{{ .Release.Namespace }}`     | Namespace the release is installed into        |
| `{{ .Chart.Name }}`            | Chart name from Chart.yaml                     |
| `{{ .Chart.Version }}`         | Chart version from Chart.yaml                  |
| `{{- ... -}}`                  | Trim whitespace before/after                   |
| `{{ if .Values.ingress.enabled }}` | Conditional block                          |
| `{{ range .Values.envVars }}`  | Loop                                           |
| `{{ include "helper" . }}`     | Call a named template from _helpers.tpl        |
| `{{ default "fallback" .Values.x }}` | Use fallback if value is empty           |
| `{{ quote .Values.something }}` | Wrap in quotes                                |
| `{{ toYaml .Values.resources \| indent 10 }}` | Convert object to YAML, indent it  |

### templates/deployment.yaml (Full Example)

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .Release.Name }}-{{ .Chart.Name }}
  namespace: {{ .Release.Namespace }}
  labels:
    app: {{ .Chart.Name }}
    version: {{ .Chart.Version }}
    release: {{ .Release.Name }}
spec:
  replicas: {{ .Values.replicaCount }}
  selector:
    matchLabels:
      app: {{ .Chart.Name }}
  template:
    metadata:
      labels:
        app: {{ .Chart.Name }}
    spec:
      containers:
        - name: {{ .Chart.Name }}
          image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}"
          imagePullPolicy: {{ .Values.image.pullPolicy }}
          ports:
            - containerPort: 3000
          env:
            - name: NODE_ENV
              value: {{ .Values.env.NODE_ENV | quote }}
            - name: LOG_LEVEL
              value: {{ .Values.env.LOG_LEVEL | quote }}
          resources:
            {{- toYaml .Values.resources | nindent 12 }}
```

### templates/service.yaml

```yaml
apiVersion: v1
kind: Service
metadata:
  name: {{ .Release.Name }}-svc
spec:
  type: {{ .Values.service.type }}
  ports:
    - port: {{ .Values.service.port }}
      targetPort: 3000
  selector:
    app: {{ .Chart.Name }}
```

### templates/ingress.yaml (Conditional)

```yaml
{{- if .Values.ingress.enabled }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-ingress
spec:
  rules:
    - host: {{ .Values.ingress.host }}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-svc
                port:
                  number: {{ .Values.service.port }}
{{- end }}
```

### templates/_helpers.tpl (Reusable Snippets)

```yaml
{{/*
Expand the name of the chart.
*/}}
{{- define "my-app.name" -}}
{{- .Chart.Name | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels applied to all resources.
*/}}
{{- define "my-app.labels" -}}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
app.kubernetes.io/name: {{ include "my-app.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}
```

Use in deployment.yaml:
```yaml
metadata:
  labels:
    {{- include "my-app.labels" . | nindent 4 }}
```

---

## Release Management

A **Release** = one installed instance of a chart in a cluster.  
Install same chart twice with different names = two independent releases.

```
helm install frontend  ./my-app          # Release: "frontend"
helm install backend   ./my-app          # Release: "backend" (same chart, isolated)
```

Each release tracks its own history, values, and rollback state.

---

## Helm 2 vs Helm 3

### Helm 2 — With Tiller (The Old Way)

```
┌─────────────────────────────────────────────────┐
│                  K8s Cluster                     │
│                                                  │
│   helm CLI ──► Tiller Pod ──► kube-apiserver    │
│   (local)      (in-cluster    (actual K8s API)  │
│                 server)                          │
│                                                  │
│   Tiller stores release history in ConfigMaps   │
└─────────────────────────────────────────────────┘
```

**Tiller** was a server-side component deployed into `kube-system` namespace.

Problems with Tiller:
- Tiller had **cluster-admin** permissions by default (full cluster access)
- Anyone who could reach Tiller could deploy anything — huge security hole
- Required `helm init` to install Tiller before any chart could be deployed
- One Tiller per cluster — bottleneck for multi-team environments
- RBAC was hard to configure correctly

Helm 2 workflow:
```bash
# First: install Tiller into cluster
helm init

# Install a chart
helm install stable/nginx --name my-nginx

# List releases
helm list

# Upgrade
helm upgrade my-nginx stable/nginx --set replicaCount=3

# Rollback
helm rollback my-nginx 1

# Delete (kept history)
helm delete my-nginx

# Delete + purge history
helm delete --purge my-nginx
```

Release history was stored in **ConfigMaps** in `kube-system` namespace.

---

### Helm 3 — No Tiller (Current Standard)

```
┌─────────────────────────────────────────────────┐
│                  K8s Cluster                     │
│                                                  │
│   helm CLI ──────────────► kube-apiserver       │
│   (local)   (direct call,   (actual K8s API)    │
│              no server)                          │
│                                                  │
│   Release history stored in Secrets (namespaced)│
└─────────────────────────────────────────────────┘
```

Tiller removed entirely. Helm 3 talks directly to kube-apiserver using your local `kubeconfig`.

Key changes from Helm 2 → 3:
| Feature               | Helm 2 (Tiller)            | Helm 3 (No Tiller)              |
|-----------------------|----------------------------|---------------------------------|
| Server component      | Tiller in kube-system      | None                            |
| Security              | Tiller = cluster-admin     | Uses your kubeconfig RBAC       |
| Release storage       | ConfigMaps in kube-system  | Secrets in release's namespace  |
| Release scoping       | Cluster-wide               | Per-namespace                   |
| `helm init`           | Required                   | Removed                         |
| `helm serve`          | Existed                    | Removed                         |
| Chart API version     | v1                         | v2                              |
| 3-way merge           | No                         | Yes (detects drift)             |
| `helm delete`         | Soft delete (kept history) | Full delete by default          |
| CRD support           | Limited                    | First-class                     |

---

## Helm 3 Commands (Current)

```bash
# Add a chart repository
helm repo add bitnami https://charts.bitnami.com/bitnami
helm repo update

# Search for charts
helm search repo nginx
helm search hub wordpress         # search Artifact Hub

# Inspect a chart before installing
helm show values bitnami/nginx
helm show chart bitnami/nginx

# Install a release
helm install my-release bitnami/nginx

# Install with custom values
helm install my-release bitnami/nginx \
  --set replicaCount=3 \
  --set image.tag=1.25.0

# Install using a values file
helm install my-release bitnami/nginx -f custom-values.yaml

# Install into specific namespace (creates namespace if --create-namespace)
helm install my-release bitnami/nginx \
  --namespace production \
  --create-namespace

# Dry run (render templates without applying)
helm install my-release ./my-app --dry-run

# Debug (show rendered YAML)
helm template my-release ./my-app

# List releases
helm list
helm list --all-namespaces

# Upgrade a release
helm upgrade my-release bitnami/nginx --set replicaCount=5

# Upgrade or install if not exists
helm upgrade --install my-release bitnami/nginx

# View release history
helm history my-release

# Rollback to previous version
helm rollback my-release 1         # rollback to revision 1
helm rollback my-release           # rollback to previous revision

# Get rendered manifests of installed release
helm get manifest my-release

# Get values used in release
helm get values my-release

# Uninstall
helm uninstall my-release

# Package your chart into a .tgz file
helm package ./my-app

# Lint chart for errors
helm lint ./my-app

# Check chart dependencies
helm dependency update ./my-app
```

---

## Override Values at Install Time

Three ways, in order of precedence (highest wins):

```bash
# 1. --set flag (inline, highest precedence)
helm install my-app ./my-app --set image.tag=2.0.0

# 2. -f values file (override file)
helm install my-app ./my-app -f prod-values.yaml

# 3. values.yaml in chart (default, lowest precedence)
```

`prod-values.yaml` (only override what changes in production):
```yaml
replicaCount: 5
image:
  tag: "3.0.1"
ingress:
  host: myapp.prod.example.com
resources:
  limits:
    cpu: 1000m
    memory: 512Mi
```

---

## Release History and Rollback Flow

```
helm install  → Revision 1
helm upgrade  → Revision 2
helm upgrade  → Revision 3  ← something broke
helm rollback → Revision 4  (a copy of Revision 2's config, applied fresh)
```

Each revision = a Secret stored in the release's namespace named `sh.helm.release.v1.<release>.v<N>`.

```bash
helm history my-release

REVISION  UPDATED                  STATUS     CHART          APP VERSION  DESCRIPTION
1         Mon May 25 10:00:00 2026 superseded my-app-1.0.0   2.0.0       Install complete
2         Mon May 25 11:00:00 2026 superseded my-app-1.1.0   2.1.0       Upgrade complete
3         Mon May 25 12:00:00 2026 failed     my-app-1.2.0   2.2.0       Upgrade failed
4         Mon May 25 12:05:00 2026 deployed   my-app-1.1.0   2.1.0       Rollback to 2
```

---

## Architecture: Full Picture

```
┌──────────────────────────────────────────────────────────────────┐
│                        Developer Machine                          │
│                                                                   │
│  values.yaml ──┐                                                  │
│  prod-vals.yaml─┤                                                 │
│                 ├─► Helm CLI (helm install/upgrade/rollback)      │
│  Chart/         │      │                                          │
│  templates/ ───┘      │ kubeconfig (your RBAC identity)          │
│                        ▼                                          │
└───────────────────────────────────────────────────────────────────┘
                          │
                          ▼
┌──────────────────────────────────────────────────────────────────┐
│                        K8s Cluster                                │
│                                                                   │
│  kube-apiserver  ◄── Helm sends rendered YAML via API            │
│        │                                                          │
│        ▼                                                          │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐              │
│  │ Deployment  │  │   Service   │  │   Ingress   │  ... created  │
│  └─────────────┘  └─────────────┘  └─────────────┘              │
│                                                                   │
│  ┌──────────────────────────────────────────────────────┐        │
│  │  Secret: sh.helm.release.v1.my-release.v1  (history)│        │
│  │  Secret: sh.helm.release.v1.my-release.v2  (history)│        │
│  └──────────────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────────────┘
```

---

## Create Your Own Chart from Scratch

```bash
# Scaffold a new chart
helm create my-app

# Edit templates/ and values.yaml

# Lint before deploying
helm lint ./my-app

# Render locally to see final YAML (no cluster needed)
helm template my-release ./my-app

# Render with override values
helm template my-release ./my-app -f prod-values.yaml

# Install to cluster
helm install my-release ./my-app -f prod-values.yaml --namespace production --create-namespace
```

---

## Common Real-World Patterns

### Pattern 1: Environment-Specific Values Files

```
my-app/
├── Chart.yaml
├── values.yaml          ← defaults (dev)
├── values.dev.yaml      ← dev overrides
├── values.staging.yaml  ← staging overrides
└── values.prod.yaml     ← prod overrides
```

```bash
# Dev
helm upgrade --install my-app ./my-app -f values.dev.yaml

# Prod
helm upgrade --install my-app ./my-app -f values.prod.yaml --namespace prod
```

### Pattern 2: Using Public Charts (Bitnami MongoDB)

```bash
helm repo add bitnami https://charts.bitnami.com/bitnami
helm install my-mongo bitnami/mongodb \
  --set auth.rootPassword=secretpass \
  --set persistence.size=10Gi
```

### Pattern 3: Chart as Dependency

`Chart.yaml`:
```yaml
dependencies:
  - name: mongodb
    version: "13.x.x"
    repository: "https://charts.bitnami.com/bitnami"
    condition: mongodb.enabled        # only install if values.mongodb.enabled = true
```

`values.yaml`:
```yaml
mongodb:
  enabled: true
  auth:
    rootPassword: "changeme"
```

```bash
helm dependency update ./my-app    # downloads mongodb chart into charts/
helm install my-release ./my-app
```

---

## Helm vs Raw kubectl: When to Use What

| Scenario                              | Use            |
|---------------------------------------|----------------|
| Learning K8s concepts                 | Raw YAML       |
| One-off debugging resources           | Raw YAML       |
| Deploying public software (Prometheus, nginx, MongoDB) | Helm chart from repo |
| Your own app across dev/staging/prod  | Your own Helm chart |
| CI/CD pipeline deployments            | Helm           |
| Rollback capability needed            | Helm           |
| Multi-environment config management   | Helm           |

---

## Quick Reference

```bash
helm install   <release> <chart>          # install
helm upgrade   <release> <chart>          # upgrade
helm upgrade --install <release> <chart>  # install or upgrade (idempotent)
helm rollback  <release> [revision]       # rollback
helm uninstall <release>                  # delete
helm list                                 # list releases
helm history   <release>                  # show revision history
helm template  <release> <chart>          # render YAML locally
helm lint      <chart>                    # validate chart
helm package   <chart>                    # pack into .tgz
```
