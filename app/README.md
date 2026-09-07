# Product Catalog App — .NET 10 + MongoDB + Angular 22

A small full-stack CRUD app: create/edit/delete/list products through a UI, backed by a REST API and MongoDB. Built to eventually run on the `k8slab` Kubernetes cluster (see `../k8s-infra-lab`).

## Domain

A **Product**: `Name`, `Description`, `Category`, `Price`, `Quantity`. Simple on purpose — the point is a clean, complete CRUD round-trip through every layer (UI form → HTTP API → MongoDB), not a complex domain.

## Structure

```
app/
  backend/ProductApi/     .NET 10 Web API (controllers + MongoDB.Driver)
  frontend/                Angular 22 app (standalone components, signals)
  scripts/                 podman helper scripts for local dev containers
  docker-compose.yml        reference description of the local stack
```

## Backend — status: done ✅

**Stack:** ASP.NET Core 10 Web API, `MongoDB.Driver` 3.11.1, Swashbuckle for interactive docs.

**Endpoints** (`ProductsController`):

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/products` | List all products |
| GET | `/api/products/{id}` | Get one product |
| POST | `/api/products` | Create |
| PUT | `/api/products/{id}` | Update |
| DELETE | `/api/products/{id}` | Delete |
| GET | `/health` | Liveness check (used later by Kubernetes probes) |

**Design notes:**
- `ProductInput` DTO is used for create/update requests — the client never sends or controls `Id` directly; MongoDB generates it.
- `ProductService` wraps `IMongoCollection<Product>` and is registered as a singleton (the underlying `MongoClient` is thread-safe and meant to be reused, not recreated per request).
- CORS is wide open (`AllowAnyOrigin`) for this local learning setup — worth tightening before anything resembling production.
- Configuration lives in `MongoDbSettings` (appsettings.json for local dev, environment variables — `MongoDbSettings__ConnectionString` etc. — for containers).

**Verified working, twice:**
1. Locally via `dotnet run` (port 5122) against a Podman-run Mongo container, full CRUD cycle via curl.
2. Fully containerized: built into a Docker image (multi-stage `Dockerfile`, `mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0`), run via Podman on a shared network with Mongo, reachable from Windows at `http://localhost:8080` thanks to WSL2's automatic localhost forwarding.

## Frontend — status: done ✅

**Stack:** Angular 22 (standalone components, signals, new `@if`/`@for` control-flow syntax), reactive forms, `provideHttpClient`.

**Structure:**
```
frontend/src/app/
  models/product.model.ts        Product / ProductInput interfaces
  services/product.ts            HttpClient wrapper — one method per endpoint
  components/product-list/       Table view: list, links to edit, delete button
  components/product-form/       One reactive form, reused for both create AND edit
  app.routes.ts                  '/', '/products/new', '/products/:id/edit'
```

**Design notes:**
- **One form component handles both create and edit.** It checks the `id` route param in `ngOnInit`; if present, it fetches the product and `patchValue`s the form, and `submit()` calls `update()` instead of `create()`. Avoids duplicating the form markup and validation.
- Uses `inject()` instead of constructor-parameter DI — required here because the reactive form is built as a class field initializer (`form = this.fb.group(...)`), which runs *before* constructor-assigned parameter properties are set. `inject()` sidesteps that ordering problem entirely.
- Delete uses a plain `confirm()` dialog — intentionally simple for this learning app.
- `API_BASE_URL` in `api-config.ts` is the single place the app points at the backend (`http://localhost:8080/api`).

**Verified working — actually driven in a real headless Chromium browser (Playwright), not just compiled:**
1. List page renders with correct empty state.
2. Create: filled the form, submitted, confirmed the new product appears in the table with correct values.
3. Edit: opened the edit form, confirmed it **pre-fills correctly** from the existing product (this initially looked broken in a first test pass due to a test-script race condition reading the field before the async fetch resolved — rechecked with network logging and it was never actually broken), changed the price, saved, confirmed the list reflects the change.
4. Delete: confirmed the `confirm()` dialog appears with the right message, accepted it, confirmed the row disappears and the empty state returns.
5. Zero console errors through the whole flow.

## Running the whole stack locally

**1. Backend + database** (commands run from Windows; they execute inside WSL2 Ubuntu, where Podman actually works — see `../k8s-infra-lab/README.md` Phase 0 for why):
```powershell
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/run-mongo.sh
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/build-api-image.sh
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/run-api.sh
```
API now at `http://localhost:8080` (Swagger UI at `/swagger`), reachable from Windows via WSL2's automatic localhost forwarding.

**2. Frontend** (Node.js is installed as a portable, per-user install — not on system PATH by default in every shell):
```powershell
$env:Path = "C:\Users\<you>\AppData\Local\Programs\nodejs-portable\node-v24.20.0-win-x64;$env:Path"
cd C:\CodeBase\app\frontend
ng serve
```
App now at `http://localhost:4200`.

## Deployed to Kubernetes — status: done ✅

The whole app now runs on the `k8slab` 3-node cluster, not just standalone containers — one API pod and one frontend pod on every node (`k8slab`, `k8slab-m02`, `k8slab-m03`), verified with a full create/list/delete cycle through the real in-cluster path. See **`k8s/README.md`** for the manifests, the image-loading workaround we needed (`minikube image load` didn't work in this environment), and how to access it from Windows (`http://localhost:8090` via port-forward).

## Next steps

- **Real persistent, replicated storage for MongoDB** (currently a single replica on minikube's default provisioner) — waits on the Longhorn phase of the infra roadmap.
- **Replace the manual port-forward** with a real Ingress/LoadBalancer once Cilium (L2 announcements) or Istio (Gateway) are in place.
- **GitOps deployment via ArgoCD** instead of manually running `kubectl apply` — once ArgoCD is set up per the infra roadmap.
