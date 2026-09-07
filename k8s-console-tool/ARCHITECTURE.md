# K8sExplorer — Architecture & Workflow

## The workflow

**1. Auth — kubeconfig, not SSH.**
[`KubeClientFactory.cs`](K8sExplorer/Services/KubeClientFactory.cs) loads a kubeconfig file (default `~/.kube/config`, or one you point at) using the exact same logic `kubectl` uses. That file just holds the API server URL + credentials (cert, token, or exec plugin).

**2. Talks straight to the API server over HTTPS.**
[`KubernetesService.cs`](K8sExplorer/Services/KubernetesService.cs) wraps the official `k8s` C# client (client-go equivalent). Every call — `ListNodeAsync`, `ListNamespacedPodAsync`, `ReadNamespacedPodLogAsync`, exec via WebSocket — hits `https://<api-server>:6443/...` directly. There's no `kubectl` binary invoked and no SSH anywhere in the code.

**3. Menus are just thin UI over that service.**
[`Program.cs`](K8sExplorer/Program.cs) → menu classes (`NodeMenu.cs`, `PodMenu.cs`, etc.) — pick a resource type, the service fetches it, [`Formatting.cs`](K8sExplorer/Display/Formatting.cs) renders it as a table.

## Why it's generic

The tool never touches the node OS. It only speaks the Kubernetes API, which is the same regardless of what's under the nodes (RHEL, RHEL 10, Ubuntu, CoreOS, EKS/GKE/AKS managed nodes...). As long as:

- the cluster's API server is reachable from wherever you run this tool, and
- your kubeconfig has valid credentials/RBAC for it,

...it works. That's why the code comments explicitly call out "no SSH, no kubectl required" and "works on managed clusters too."

## vs. a typical SSH + kubectl workflow

| Your current pattern | This tool |
|---|---|
| SSH to a node | Nothing to SSH to — connects once, to the API server |
| Run `kubectl get pods`, `crictl`, `journalctl`, etc. on that node | Calls the equivalent API endpoints directly from your machine |
| Node-level logs (`journalctl`, syslog) | **Not available** — only `kubectl logs`-equivalent (container stdout/stderr) and Kubernetes Events. Node/kubelet/system logs are deliberately outside the K8s API for security reasons, so OS-level troubleshooting still needs SSH + `journalctl` |
| Depends on which node you land on | Cluster-wide view regardless of node count |

## RHEL 10 specifically

Yes, it works — RHEL 10 vs. RHEL 8/9 vs. anything else makes zero difference here, since the tool doesn't care about node OS at all, only about the Kubernetes API version. The only things that matter:

- The cluster's API server is network-reachable (port 6443 typically) from wherever you run K8sExplorer.
- Your kubeconfig/RBAC user has permission for the resources you're browsing (nodes, pods, deployments, services, events, pod exec/logs).
- The `k8s` C# client library version is reasonably compatible with your API server's version (standard K8s API compatibility skew, unrelated to RHEL).

**Gap worth knowing:** it can't fetch OS/kubelet-level diagnostics (disk pressure detail, systemd unit status, container runtime logs outside K8s) — that still needs SSH to the RHEL 10 node itself.
