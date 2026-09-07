# Complete Setup Guide — From Zero to a Running App on Kubernetes

Everything done in this project, in the order it actually happened, with every command and every problem hit along the way. This is the single consolidated record; `k8s-infra-lab/README.md`, `app/README.md`, and `app/k8s/README.md` cover the same ground in more focused, topic-specific form if you want a shorter read on one piece.

**End state:** a 3-node Kubernetes cluster (`k8slab`) running entirely inside WSL2 Ubuntu via Podman + CRI-O, hosting a full CRUD app (.NET 10 API + MongoDB + Angular 22 UI), with one API pod and one frontend pod scheduled on every node.

---

## Part 1 — Environment discovery and why native Windows Podman had to be abandoned

**Starting point:** Windows 10 host, Podman and minikube already installed, nothing running yet.

### 1.1 Checked what was actually there
```powershell
podman --version                 # 6.1.0
podman machine list              # a machine existed, LAST UP: Never (11 days old, never started)
minikube version                 # v1.38.1
kubectl / helm                   # not installed
```

### 1.2 Diagnosed why the podman machine never started
Windows Podman runs a Fedora-based VM ("podman machine") inside WSL2. Tried to start it:
```powershell
podman machine start
# Error: machine did not transition into running state: ssh error: machine not in running state
```
Investigated by booting the machine's WSL distro directly and inspecting its init:
```powershell
wsl -d podman-machine-default -u root -- cat /root/bootstrap
```
Found the machine boots via an `unshare`-based fake-init that launches `systemd` — because this WSL2 platform's cgroup setup is **hybrid v1** (checked via `mount | grep cgroup`), and this Fedora image's systemd requires unified **cgroup v2**. Systemd exited instantly and silently on every attempt. Confirmed the underlying `wsl.exe` here is the old Windows-inbox build (`10.0.21996.1`), not the modern Microsoft Store WSL package, which is why cgroup v2 support isn't available.

Tried recreating the machine from scratch (`podman machine rm` + `podman machine init --cpus 6 --memory 10240 --disk-size 60`) — same failure. Confirmed via a control test that the separate "Ubuntu" WSL distro had the identical limitation (`systemctl is-system-running` → `offline`, `PID 1` → generic `/init`, not `systemd`).

**Decision:** abandon `podman machine` (the Windows-integration VM layer) entirely. Run Podman **natively inside the existing WSL2 "Ubuntu" distro** instead — it's already a real Linux kernel environment, no nested VM/systemd dance required for Podman itself.
```powershell
podman machine rm podman-machine-default -f
```

### 1.3 Sized WSL2 for the whole roadmap
Default WSL2 memory was only 7.7GB (auto = 50% of 16GB host RAM). Raised the ceiling via `C:\Users\avskm\.wslconfig`:
```ini
[wsl2]
memory=12GB
processors=8
swap=4GB
localhostForwarding=true
```
Applied with `wsl --shutdown` (full shutdown, not just `wsl -t Ubuntu`, since `.wslconfig` is read by the WSL2 launcher itself). Verified: `free -h` inside Ubuntu now showed 11GB available, 8 CPUs.

### 1.4 Installed Podman inside Ubuntu, hit a kernel NAT gap
```bash
apt-get update -qq
apt-get install -y -qq podman uidmap curl conntrack
```
Podman 4.9.3 installed fine. But any container on a bridge network failed:
```
Error: netavark: unable to append rule '-d 10.88.0.0/16 -j ACCEPT' to table 'nat':
code: 4, msg: iptables v1.8.10 (nf_tables): RULE_APPEND failed (No such file or directory)
```
Root cause: this WSL2 kernel (`5.10.16.3-microsoft-standard-WSL2`) has incomplete **nf_tables NAT** support. minikube's podman driver *always* creates a dedicated bridge network for its nodes, so this blocked cluster creation entirely — rootless or rootful, it didn't matter.

**Tried the "correct" fix first:** download and install the modern Microsoft Store WSL package (ships a kernel with full netfilter support), straight from `github.com/microsoft/WSL` releases:
```powershell
Invoke-WebRequest -Uri "https://github.com/microsoft/WSL/releases/download/2.7.13/Microsoft.WSL_2.7.13.0_x64_ARM64.msixbundle" -OutFile "$env:TEMP\Microsoft.WSL.msixbundle"
Add-AppxPackage -Path "$env:TEMP\Microsoft.WSL.msixbundle"
# Error: Administrator privileges required to install packaged service
```
No admin rights available in this session. Also tried `wsl --update` (non-admin) — reported "No updates are available," which turned out to be a false negative; the update genuinely does need elevation to actually apply.

**Actual fix used:** the kernel's *legacy* xtables NAT path (as opposed to the nftables-compat shim `iptables-nft`) works fine — it was specifically the nftables translation layer that was broken, not NAT support itself:
```bash
update-alternatives --set iptables /usr/sbin/iptables-legacy
update-alternatives --set ip6tables /usr/sbin/ip6tables-legacy
```
Verified with a raw `iptables -t nat -A ... -j ACCEPT` test (worked, exit 0), then a real `podman network create` + bridge-networked container run (worked, no NAT errors).

*(If admin access is ever available, `wsl --update` as Administrator + `wsl --shutdown` is the "proper" fix and likely makes this workaround unnecessary — but it's harmless to leave in place either way.)*

### 1.5 `/lib/modules` didn't exist
minikube's podman driver bind-mounts `/lib/modules:/lib/modules:ro` into each node container. WSL2's kernel has no real module files (everything's compiled in, not loaded as `.ko` files), so the mount source didn't exist:
```
Error: statfs /lib/modules: no such file or directory
```
Fixed with a stub directory:
```bash
mkdir -p /lib/modules/$(uname -r)
```

### 1.6 Rootless Podman vs. the CRI-O kicbase image
Best practice is rootless Podman. Created a `dev` user with proper subuid/subgid ranges:
```bash
adduser --disabled-password --gecos "" dev
usermod -aG sudo dev
echo 'dev ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/dev
chmod 440 /etc/sudoers.d/dev
loginctl enable-linger dev
```
Verified: `su - dev -c "podman run --rm hello-world"` worked correctly.

But starting the actual minikube cluster as this rootless user failed differently:
```
Failed to enable unit, unit containerd-fuse-overlayfs.service does not exist.
```
minikube's kicbase image's init script assumes a systemd unit for fuse-overlayfs storage (needed by rootless containers lacking native overlayfs) that isn't present in the CRI-O variant of that image — a minikube/kicbase packaging gap for the podman+CRI-O+rootless combination specifically.

**Decision:** run the minikube cluster as **root** inside WSL2 Ubuntu instead. Since this WSL2 Ubuntu is a disposable, single-user local dev sandbox (not shared or production), the reduced isolation is an acceptable tradeoff for a working cluster.

---

## Part 2 — Building the 3-node cluster

### 2.1 Installed tooling
```bash
curl -Lo minikube https://storage.googleapis.com/minikube/releases/latest/minikube-linux-amd64
install minikube /usr/local/bin/minikube

KVER=$(curl -Ls https://dl.k8s.io/release/stable.txt)
curl -Lo kubectl "https://dl.k8s.io/release/${KVER}/bin/linux/amd64/kubectl"
install kubectl /usr/local/bin/kubectl
```
Result: minikube v1.39.0, kubectl v1.37.0.

### 2.2 Smoke-tested with a single node first
```bash
minikube start -p smoketest --driver=podman --container-runtime=crio --cpus=2 --memory=2200mb --disk-size=10g --force
```
(`--force` needed because minikube refuses the podman driver as root by default — a safety check we deliberately override for the reasons in 1.6.)

First attempt failed on the `/lib/modules` issue (1.5, fixed then retried). Second attempt failed with the rootless fuse-overlayfs issue (1.6, switched to root then retried). Third attempt succeeded — node came up `Ready` with CRI-O 1.35.7, kindnet CNI, kube-proxy, CoreDNS, etcd, all control-plane pods `Running`.

### 2.3 Deleted the smoke test, created the real 3-node cluster
```bash
minikube delete -p smoketest
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
All 12 `kube-system` pods `Running`, 0 restarts. No taints on the control-plane node (minikube leaves it schedulable — relevant later for spreading app pods across all 3 nodes including the control-plane).

**Architecture note:** these "3 nodes" are 3 Podman containers, all running inside the *same* single WSL2 Ubuntu VM — not 3 separate VMs. This is how minikube's podman/docker driver works generally (same model as `kind`).

### 2.4 A stability incident (worth recording)
Partway through later work, `k8slab-m02` went `NotReady` with taint `node.kubernetes.io/unreachable:NoExecute` and message "Kubelet stopped posting node status." At the same time, basic commands like `podman ps` and `free -h` inside the WSL2 VM started hanging for 15-30+ seconds. Diagnosis pointed at resource pressure — everything was running at once at that point: 3 K8s nodes, a standalone Mongo container, a standalone API container, `ng serve`, and a headless Chromium UI test, all sharing the 12GB WSL2 VM.

It self-recovered a few minutes later with no intervention needed — rechecked and all 3 nodes were back to `Ready`, 0 restarts on any pod. Kept as a signal to size things conservatively (see the resource requests/limits in the app's Kubernetes manifests, Part 5) and to expect this class of hiccup on a resource-constrained local VM rather than be alarmed by it.

---

## Part 3 — Backend: .NET 10 Web API + MongoDB

**Domain chosen:** a simple Product Catalog (`Name`, `Description`, `Category`, `Price`, `Quantity`) — deliberately simple so the point is a clean, complete CRUD round-trip through every layer, not a complex domain.

### 3.1 Scaffolded the project
```powershell
dotnet new webapi -n ProductApi -controllers
```
(.NET 10 SDK, `10.0.400`, was already installed natively on Windows.) Removed the template's `WeatherForecast.cs` / `WeatherForecastController.cs` boilerplate.

### 3.2 Added MongoDB.Driver
```bash
dotnet add package MongoDB.Driver       # 3.11.1
dotnet add package Swashbuckle.AspNetCore   # 10.2.3, for Swagger UI
```

### 3.3 Wrote the application code
- `Models/Product.cs` — the entity, with `[BsonId]`/`[BsonRepresentation(BsonType.ObjectId)]` on `Id`.
- `Models/ProductDtos.cs` — `ProductInput`, the create/update DTO (the client never sends or controls `Id`; MongoDB generates it), with data-annotation validation (`Required`, `StringLength`, `Range`).
- `Settings/MongoDbSettings.cs` — `ConnectionString`, `DatabaseName`, `ProductsCollectionName`.
- `Services/ProductService.cs` — wraps `IMongoCollection<Product>`, one method per CRUD op, registered as a **singleton** (the underlying `MongoClient` is thread-safe and meant to be reused, not recreated per request).
- `Controllers/ProductsController.cs` — `GET /api/products`, `GET /api/products/{id}`, `POST`, `PUT /{id}`, `DELETE /{id}`.
- `Program.cs` — wired up `MongoDbSettings` config binding, CORS (`AllowAnyOrigin`, fine for this local learning setup), Swagger (`/swagger`), and a `GET /health` endpoint (used later by Kubernetes probes).
- `appsettings.json` — local-dev default `ConnectionString: mongodb://localhost:27017`, overridden via env var (`MongoDbSettings__ConnectionString`) in containers.

**One build error hit and fixed:** `Program.cs` initially used `using Microsoft.OpenApi.Models;` for `OpenApiInfo` — Swashbuckle 10.x moved this type, the correct namespace is `using Microsoft.OpenApi;`.

### 3.4 Verified locally, twice
1. **Bare `dotnet run`** (port 5122) against a Podman-run Mongo container:
   ```bash
   dotnet build   # 0 warnings, 0 errors
   dotnet run --urls http://localhost:5122
   ```
   Full CRUD cycle via curl — create → list → get → update → get → delete → list — all correct.

2. **Fully containerized.** Wrote `Dockerfile` (multi-stage: `mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0`, `ENV ASPNETCORE_URLS=http://+:8080`), and `.dockerignore` (`bin/`, `obj/`). Ran Mongo and the API as Podman containers on a shared network, inside WSL2:
   ```bash
   # scripts/run-mongo.sh
   podman network create appnet
   podman run -d --name mongo-dev --network appnet --network-alias mongo \
     -p 27017:27017 -v mongo-dev-data:/data/db docker.io/library/mongo:7

   # scripts/build-api-image.sh
   cd /mnt/c/CodeBase/app/backend/ProductApi && podman build -t product-api:dev .

   # scripts/run-api.sh
   podman run -d --name product-api-dev --network appnet -p 8080:8080 \
     -e MongoDbSettings__ConnectionString="mongodb://mongo:27017" \
     -e MongoDbSettings__DatabaseName="ProductCatalog" \
     product-api:dev
   ```
   Reachable from **Windows** at `http://localhost:8080` thanks to WSL2's automatic localhost forwarding. Full CRUD cycle re-verified through the container.

---

## Part 4 — Frontend: Angular 22

### 4.1 Installed Node.js (no admin rights available)
Found the latest LTS via the official dist index, downloaded the **portable zip** distribution (no installer, no admin needed):
```powershell
Invoke-WebRequest -Uri "https://nodejs.org/dist/v24.20.0/node-v24.20.0-win-x64.zip" -OutFile "$env:TEMP\node.zip"
Expand-Archive -Path "$env:TEMP\node.zip" -DestinationPath "$env:LOCALAPPDATA\Programs\nodejs-portable" -Force
[Environment]::SetEnvironmentVariable("Path", "<nodeDir>;$existingUserPath", "User")
```
Node v24.20.0 / npm 11.19.0. **Caveat hit:** the registry PATH change only affects *new* process trees — this session's already-running shell tree kept its old inherited environment, so every subsequent command in this session had to explicitly prefix `$env:Path = "<nodeDir>;$env:Path"`. A fresh terminal opened by the user picks up the permanent PATH change with no extra step.

### 4.2 Installed Angular CLI and scaffolded the app
```powershell
npm install -g @angular/cli@latest      # Angular CLI 22.1.7
ng new frontend --routing --style=scss --skip-git --ssr=false --package-manager=npm --defaults
```
Standalone-components app (no NgModules), Angular's new `@if`/`@for` control-flow syntax, SCSS, routing enabled, SSR explicitly disabled (kept simple for a learning CRUD app).

### 4.3 Built out the CRUD UI
```powershell
ng generate service services/product
ng generate component components/product-list
ng generate component components/product-form
```
- `models/product.model.ts` — `Product` / `ProductInput` interfaces.
- `services/product.ts` — `HttpClient`-based, one method per endpoint.
- `components/product-list/` — table view: list, category badges, Edit link, Delete button (`confirm()` dialog).
- `components/product-form/` — **one reactive form reused for both create and edit.** Checks the `id` route param in `ngOnInit`; if present, fetches the product and `patchValue`s the form, and `submit()` calls `update()` instead of `create()`. Avoids duplicating form markup/validation.
- `app.routes.ts` — `''` (list), `'products/new'`, `'products/:id/edit'`.
- `app.config.ts` — added `provideHttpClient(withFetch())`.
- `api-config.ts` — single constant for the backend base URL (see Part 5.1 for how this evolved).

**Two build errors hit and fixed:**
1. `TS2729: Property 'fb' is used before its initialization` — the reactive form was a class field initializer (`form = this.fb.group(...)`) referencing a constructor-injected `fb`, but field initializers run *before* constructor-assigned parameter properties are set. Fixed by switching to Angular's `inject()` function instead of constructor DI for all four dependencies in `ProductForm` — `inject()` works correctly in field initializers.
2. `TS2349: This expression is not callable` on `request.subscribe(...)` — a ternary between `create()` and `update()` produced a union `Observable` type that TypeScript couldn't resolve overloaded `subscribe` signatures for. Fixed by restructuring to an explicit `if/else` with shared `onSuccess`/`onError` handlers instead of one ternary + shared `.subscribe()`.

Also fixed the auto-generated `*.spec.ts` files, which referenced stale class names/missing providers (`HttpClient`, `Router`) after the above changes — added `provideHttpClient()`, `provideHttpClientTesting()`, `provideRouter([])` to each.

### 4.4 Styled it
First pass: clean but plain (flat gray background, basic table/buttons). User feedback: *"make UI little fancy it very white and plain."* Redesigned:
- Added Google Font (Plus Jakarta Sans) via `index.html`.
- Purple→pink gradient soft background, gradient "pill" buttons with hover-lift, category badges, teal price highlighting, card shadows, rounded corners, a 🛍️ icon accent on the heading, and a friendlier empty state.

### 4.5 Actually verified in a real browser (not just "it compiles")
Per the standing instruction to test UI changes in a browser before claiming done: no `chromium-cli` tool was available, so installed Playwright directly as a throwaway scratchpad project and drove real headless Chromium against the running `ng serve` instance:
```bash
npm install playwright
npx playwright install chromium
```
Wrote a script (`test.mjs`) that: navigates to the list page, screenshots, clicks "+ Add Product," fills the form, submits, screenshots the updated list, clicks "Edit," verifies the form pre-fills from the existing product, changes a field, saves, screenshots the update, clicks "Delete," accepts the `confirm()` dialog, screenshots the empty state again, and checks for any browser console errors.

**One real scare, resolved:** the first run showed the edit form's `Name` field reading empty right after navigation — looked like a real pre-fill bug. Added network request/response logging and reran: the `GET /api/products/{id}` call **did** fire and returned correctly; the form **did** pre-fill correctly (confirmed by the following screenshot). The apparent bug was a test-script race condition — reading the field's value before the async fetch had resolved, not an application bug. Re-verified visually via screenshots that Create, Edit (correct pre-fill), and Delete all work, with zero console errors throughout.

---

## Part 5 — Deploying onto the Kubernetes cluster

### 5.1 Made the API URL environment-agnostic
Changed `api-config.ts` from an absolute `http://localhost:8080/api` to a relative `/api`, which works unchanged in three different environments via three different proxy mechanisms:
- **`ng serve`:** `frontend/proxy.conf.json` (`{"/api": {"target": "http://localhost:8080", "secure": false}}`), wired into `angular.json`'s `serve.options.proxyConfig`.
- **Docker container / Kubernetes:** `nginx.conf` (below) reverse-proxies `/api/*` to the backend Service.

### 5.2 Dockerized the frontend
```dockerfile
FROM docker.io/library/node:24-alpine AS build
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
RUN npx ng build --configuration production

FROM docker.io/library/nginx:alpine AS final
COPY --from=build /src/dist/frontend/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 8080
```
`nginx.conf`: serves the SPA with `try_files $uri $uri/ /index.html` (so client-side routes survive a hard refresh), and:
```nginx
location /api/ {
    proxy_pass http://product-api:8080/api/;
}
```
(`product-api` resolves via Kubernetes' in-cluster DNS once deployed — the Service name.)

**Build error hit and fixed:** the first build failed with `short-name "nginx:alpine" did not resolve to an alias and no unqualified-search registries are defined in "/etc/containers/registries.conf"`. Podman's shortname-alias list (used for images like `mongo`, `node`, `hello-world`) didn't include an alias for `nginx`. Fixed by using the fully-qualified name `docker.io/library/nginx:alpine` (and `docker.io/library/node:24-alpine` for consistency) instead of the short form.

### 5.3 Built both images
```bash
podman build -t product-frontend:dev .    # cd frontend/ first
# product-api:dev already built in Part 3.4
```

### 5.4 Getting images into the cluster — the second real workaround of this project
```bash
minikube image load product-api:dev -p k8slab
# The image 'product-api:dev' was not found; unable to add it to cache.
```
Failed regardless of image-name form tried (`localhost/product-api:dev`, `product-api:dev`), and failed even for a known-good already-pulled image (`docker.io/library/mongo:7`) — pointing at a podman-socket detection issue specific to this environment, not a naming problem.

**Workaround:** CRI-O and Podman inside minikube's kicbase node containers share the same underlying `containers/storage` backend. An image loaded via `podman load` **inside a node container** becomes visible to CRI-O directly:
```bash
for img in product-api:dev product-frontend:dev; do
  for node in k8slab k8slab-m02 k8slab-m03; do
    podman save "localhost/$img" | podman exec -i "$node" podman load
  done
done
```
Verified on every node via `podman exec <node> crictl images | grep product` — both images present on all 3 nodes. This is why every Deployment manifest sets `imagePullPolicy: Never` — the image is already local; Kubernetes must not try to pull it from a registry.

### 5.5 Wrote the Kubernetes manifests (`app/k8s/`)
| File | Contents |
|---|---|
| `00-namespace.yaml` | `product-catalog` namespace |
| `10-mongo.yaml` | MongoDB Deployment (1 replica — real replication deferred to the infra roadmap's Longhorn phase), `PersistentVolumeClaim` (1Gi, minikube's default `storage-provisioner`), Service |
| `20-api.yaml` | API Deployment, **3 replicas**, `imagePullPolicy: Never`, env vars pointing at `mongodb://mongo:27017`, `/health` readiness+liveness probes, resource requests/limits (`50m`/`100Mi` request, `250m`/`256Mi` limit) |
| `30-frontend.yaml` | Frontend Deployment, **3 replicas**, `imagePullPolicy: Never`, `/` readiness probe, resource requests/limits (`20m`/`32Mi` request, `100m`/`128Mi` limit), Service of `type: NodePort` on `30080` |

**The key detail that makes "spread across all 3 nodes" actually true rather than incidental:** both the API and frontend Deployments include a `topologySpreadConstraints` block:
```yaml
topologySpreadConstraints:
  - maxSkew: 1
    topologyKey: kubernetes.io/hostname
    whenUnsatisfiable: ScheduleAnyway
    labelSelector:
      matchLabels:
        app: product-api   # or product-frontend
```
Without this, the scheduler's default bin-packing behavior *tends* to spread pods reasonably evenly but doesn't guarantee it.

### 5.6 Deployed and verified
```bash
kubectl apply -f 00-namespace.yaml
kubectl apply -f 10-mongo.yaml
kubectl apply -f 20-api.yaml
kubectl apply -f 30-frontend.yaml
kubectl -n product-catalog rollout status deployment/mongo --timeout=120s
kubectl -n product-catalog rollout status deployment/product-api --timeout=120s
kubectl -n product-catalog rollout status deployment/product-frontend --timeout=120s
```
**Result — verified with `kubectl -n product-catalog get pods -o wide`:**
```
NAME                               READY   STATUS    NODE
mongo-64f55f9cc4-2lmkd             1/1     Running   k8slab-m03
product-api-84955bd674-2xdj4       1/1     Running   k8slab
product-api-84955bd674-4dhw5       1/1     Running   k8slab-m02
product-api-84955bd674-sthdz       1/1     Running   k8slab-m03
product-frontend-7c4869767-kfhdm   1/1     Running   k8slab-m03
product-frontend-7c4869767-lq9z7   1/1     Running   k8slab-m02
product-frontend-7c4869767-zlhqs   1/1     Running   k8slab
```
Exactly one API pod and one frontend pod per node. All `Running`, 0 restarts.

**Functional verification, three layers:**
1. NodePort load-balancing: `curl http://<node-ip>:30080/` returned `200` from all three node IPs (`192.168.49.2`, `.3`, `.4`).
2. Full path through the real reverse proxy: `POST`/`GET`/`DELETE` against `http://192.168.49.2:30080/api/products` — request flows browser → frontend pod's nginx → `/api` proxy → `product-api` Service → one of 3 API pods → `mongo` Service → the Mongo pod — worked correctly, data persisted and read back accurately.
3. Cleaned up the test record afterward (`DELETE` on the created id) to leave the deployed app's data clean.

### 5.7 Making it reachable from Windows
The NodePort IPs (`192.168.49.x`) live inside the WSL2 network namespace and aren't directly reachable from a Windows browser. Bridged with `kubectl port-forward`, relying on WSL2's automatic localhost forwarding to do the rest:
```bash
kubectl -n product-catalog port-forward svc/product-frontend 8090:8080 --address 0.0.0.0
```
App now reachable at **`http://localhost:8090`** from Windows. This stands in for a real Ingress/LoadBalancer, which is one of the next infra-roadmap phases (Cilium L2 announcements, then Istio Gateway).

---

## Where everything lives

```
C:\CodeBase\
  k8s-infra-lab\
    README.md              full infra roadmap: Helm, Cilium, Longhorn, cert-manager,
                            Sealed Secrets, Istio, ArgoCD, Argo Workflows/Events, MongoDB HA
    docs\cluster-status.md  point-in-time node/pod snapshot with explanations
    scripts\                the 3 scripts that built the base cluster (Part 1-2 above)

  app\
    README.md               backend + frontend status, design notes, how to run locally
    backend\ProductApi\      .NET 10 Web API (Part 3)
    frontend\                Angular 22 app (Part 4)
    k8s\                     Kubernetes manifests + deployment README (Part 5)
    scripts\                 podman/minikube helper scripts used throughout Part 3 & 5
    docker-compose.yml       reference description of the local (non-k8s) dev stack

  COMPLETE-SETUP-GUIDE.md   this file
```

## Quick reference — commands you'll actually reuse

```powershell
# Cluster status
wsl -d Ubuntu -u root -- kubectl get nodes -o wide
wsl -d Ubuntu -u root -- kubectl -n product-catalog get pods -o wide

# App access (if the port-forward isn't already running)
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/port-forward-frontend.sh
# then browse http://localhost:8090

# Rebuild + redeploy after a code change
#   (rebuild the relevant image first, then:)
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/load-images-to-cluster.sh
wsl -d Ubuntu -u root -- kubectl -n product-catalog rollout restart deployment/product-api
wsl -d Ubuntu -u root -- kubectl -n product-catalog rollout restart deployment/product-frontend

# Local (non-cluster) dev loop, e.g. for fast backend iteration
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/run-mongo.sh
cd C:\CodeBase\app\backend\ProductApi && dotnet run --urls http://localhost:5122
```

## What's next

See `k8s-infra-lab/README.md`'s roadmap table: Helm → Cilium (real CNI + LoadBalancer, replacing the manual port-forward) → HPA → Longhorn (real Mongo persistence/backup/replication) → cert-manager → Sealed Secrets → Istio → ArgoCD (GitOps instead of manual `kubectl apply`) → Argo Workflows → Argo Events → MongoDB HA.
