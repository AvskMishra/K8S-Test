# K8sExplorer — End-to-End Flow: Local → Git → CI/Kaniko → GitOps → RHEL 10 Node

## Flow diagram

```
┌─────────────┐   git push    ┌──────────────┐  webhook/poll  ┌───────────────────┐
│ Your Windows │──────────────▶│  Git repo    │───────────────▶│  CI pipeline       │
│ (dotnet code)│               │ (source +    │                │  runs Kaniko       │
└─────────────┘               │  Dockerfile) │                └─────────┬──────────┘
                                └──────────────┘                          │ builds image,
                                                                           │ no docker daemon
                                                                           ▼
                                                                 ┌───────────────────┐
                                                                 │  Docker registry   │
                                                                 │  k8sexplorer:<tag> │
                                                                 └─────────┬──────────┘
                                                                           │ new tag detected
                                                                           ▼
┌──────────────┐  git commit   ┌──────────────┐   watches      ┌───────────────────┐
│ deploy/       │◀─────────────│ CI or image-  │◀───────────────│ Argo CD / Flux      │
│ kustomization │  (tag bump)  │ automation    │   registry     │ (in-cluster        │
│ .yaml         │──────────────▶│ controller    │                │  controller)        │
└──────────────┘   sees change └──────────────┘                └─────────┬──────────┘
                                                                           │ sync (apply)
                                                                           ▼
                                                          ┌─────────────────────────────┐
                                                          │  Kubernetes API server        │
                                                          │  applies deploy/*.yaml:       │
                                                          │  Namespace, ServiceAccount,   │
                                                          │  ClusterRole(Binding),        │
                                                          │  Deployment                   │
                                                          └─────────────┬───────────────┘
                                                                        │ scheduler places pod
                                                                        ▼
                                                          ┌─────────────────────────────┐
                                                          │  RHEL 10 node                │
                                                          │  pod: k8sexplorer            │
                                                          │  container idles on          │
                                                          │  `sleep infinity`             │
                                                          │  (no dotnet installed on the │
                                                          │   node itself — irrelevant,   │
                                                          │   it's inside the container)  │
                                                          └─────────────┬───────────────┘
                                                                        │ you attach when needed
                                                                        ▼
                                                          kubectl exec -it -n k8sexplorer
                                                          deploy/k8sexplorer -- dotnet K8sExplorer.dll
```

## Step by step, tied to the actual files

**1. You write/change code locally** ([`KubernetesService.cs`](K8sExplorer/Services/KubernetesService.cs), etc.) and `git push`. Nothing cluster-specific here — same repo, same Dockerfile as before.

**2. CI triggers on the push** and runs a Kaniko build using [`Dockerfile`](Dockerfile) at the repo root — Kaniko builds the image *without* needing a privileged Docker daemon (the reason it exists at all for in-cluster CI), then pushes to your registry, e.g. `your-registry.example.com/k8sexplorer:<git-sha>`.

**3. The new image tag reaches [`deploy/kustomization.yaml`](deploy/kustomization.yaml)** one of two ways:
- **CI-driven bump**: the CI job itself runs `kustomize edit set image ...` and commits/pushes that change back to the manifest path.
- **Registry-driven bump**: Flux's Image Automation Controller or Argo CD Image Updater polls the registry directly and commits the tag bump itself — no CI step needed for this part.

**4. Argo CD / Flux notices the Git change** in `deploy/` and syncs it — applies [`namespace.yaml`](deploy/namespace.yaml), [`rbac.yaml`](deploy/rbac.yaml) (ServiceAccount + ClusterRole/Binding), and [`deployment.yaml`](deploy/deployment.yaml) to the cluster's API server. This is the actual "GitOps" step — a controller *inside* the cluster reconciling desired state from Git, not you running `kubectl apply` by hand.

**5. The scheduler places the pod** — potentially on your RHEL 10 node, like any other pod. The node needs nothing installed; the container already has the .NET runtime baked in from the Dockerfile's `mcr.microsoft.com/dotnet/runtime:10.0` base layer. The container's command is `sleep infinity` — it just sits there holding the ServiceAccount token, not running the interactive app yet.

**6. You attach to actually use it:**
```
kubectl exec -it -n k8sexplorer deploy/k8sexplorer -- dotnet K8sExplorer.dll
```
This is the moment [`KubeClientFactory.cs`](K8sExplorer/Services/KubeClientFactory.cs) runs. `kubeconfigPath` is null (nobody passed one), `IsInCluster()` returns true (because it's genuinely running as a pod — `KUBERNETES_SERVICE_HOST` is set, the token is mounted), so it calls `InClusterConfig()` and authenticates as the `k8sexplorer` ServiceAccount, scoped exactly to what [`rbac.yaml`](deploy/rbac.yaml) grants (read nodes/pods/services/events/namespaces, read logs, create exec). From here the app behaves identically to the local/podman versions — same menus, same `KubernetesService.cs` calls — it just talks to `https://kubernetes.default.svc` instead of an external API endpoint from a kubeconfig.

## Standalone podman vs. in-cluster GitOps

| | Standalone podman (node-local) | In-cluster GitOps |
|---|---|---|
| Auth | kubeconfig file mounted into the container | ServiceAccount token, auto-mounted by Kubernetes |
| Who manages the running container | You, manually (`podman pull && run`) | Argo CD/Flux, continuously reconciled from Git |
| Where it runs | Any node you choose, outside K8s scheduling | Wherever the scheduler places the pod |
| How you interact | `podman run -it` directly | `kubectl exec -it` into the idling pod |

Both paths use the exact same image and code — the only difference is which branch of `KubeClientFactory.Create()` fires, based on whether a kubeconfig path was given. See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the general app architecture and [`PLAN.md`](PLAN.md) for the original design log.
