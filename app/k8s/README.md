# Deploying the app onto `k8slab`

Moves the Product Catalog app from standalone Podman containers onto the actual 3-node `k8slab` Kubernetes cluster, with pods spread across all three nodes.

## What changed for Kubernetes vs. local Podman

- **Frontend is now Dockerized** (it wasn't before): multi-stage build — `node:24-alpine` builds the Angular production bundle, then `nginx:alpine` serves it. `nginx.conf` also reverse-proxies `/api/*` to the `product-api` Service internally, so the browser only ever talks to one origin (no CORS juggling, no hardcoded backend URL). `api-config.ts` was changed from an absolute `http://localhost:8080/api` to a relative `/api` — this works unchanged in three environments: `ng serve` (via `proxy.conf.json`), the local Docker container, and the cluster.
- **3 replicas each**, for API and frontend, with an explicit `topologySpreadConstraints` (`topologyKey: kubernetes.io/hostname`) — this is what actually guarantees one pod per node rather than leaving it to chance.
- **MongoDB stays at 1 replica** — real replication/HA for Mongo is intentionally deferred to the infra roadmap's Longhorn phase (`../../k8s-infra-lab/README.md`, phase 12), once proper persistent, replicated storage exists. For now it uses minikube's default `storage-provisioner` via a plain `PersistentVolumeClaim`.

## Getting images into the cluster

**`minikube image load` did not work in this environment** — it couldn't resolve locally-built images regardless of naming, most likely a podman-socket detection issue specific to this WSL2/podman setup. Root-caused and worked around: since CRI-O and Podman inside minikube's kicbase node containers share the same underlying `containers/storage` backend, an image loaded via `podman load` **inside a node container** becomes visible to CRI-O directly. So instead:

```bash
podman save localhost/product-api:dev | podman exec -i k8slab podman load
# ...repeated for every node × every image (see scripts/load-images-to-cluster.sh)
```

This is why every Deployment sets `imagePullPolicy: Never` — the image is already local to each node; Kubernetes must not try to pull it from a registry.

## Manifests (applied in order)

| File | What |
|---|---|
| `00-namespace.yaml` | `product-catalog` namespace |
| `10-mongo.yaml` | MongoDB Deployment (1 replica) + PVC + Service |
| `20-api.yaml` | .NET API Deployment (3 replicas, spread across nodes) + Service, with `/health` liveness/readiness probes |
| `30-frontend.yaml` | Angular/nginx Deployment (3 replicas, spread across nodes) + NodePort Service (30080) |

## Verified result

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

One API pod and one frontend pod on every node, as intended. Confirmed NodePort load-balances correctly by hitting the frontend on all three node IPs (`192.168.49.2/3/4:30080`) — all returned `200`. Ran a full create → list → delete cycle through the real path (browser → frontend NodePort → nginx `/api` proxy → `product-api` Service → a pod → `mongo` Service → the Mongo pod) — worked end-to-end.

## Accessing it

The cluster's NodePort IPs (`192.168.49.x`) live inside the WSL2 network namespace and aren't reachable directly from Windows. A `kubectl port-forward` bridges that gap (WSL2's automatic localhost forwarding does the rest):

```powershell
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/port-forward-frontend.sh
```

Then open **http://localhost:8090** from Windows. This is a stand-in for a real Ingress/LoadBalancer — one of the infra roadmap's next phases (Cilium's L2 announcements, then Istio's Gateway) will replace this with something that doesn't need a manual port-forward.

## Redeploying after a code change

```bash
# rebuild the changed image, then:
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/load-images-to-cluster.sh
wsl -d Ubuntu -u root -- kubectl -n product-catalog rollout restart deployment/product-api      # or product-frontend
```
