# K8sExplorer — Plan, Reasoning, and Implementation Log

A .NET console app that browses any Kubernetes cluster — nodes, pods, deployments, services, events, pod logs, and exec — entirely through the Kubernetes API server. No `kubectl` shelled out to, no SSH to any node or the cluster host, ever.

---

## 1. Package choices

### Talking to the cluster: `KubernetesClient` (NuGet id, namespace `k8s`)
This is the official, CNCF-recognized C# client for the Kubernetes API. Chosen because:
- It **deserializes API responses directly into typed C# classes** (`V1Node`, `V1Pod`, `V1Deployment`, ...) — this is exactly the "convert this object to a dotnet class" requirement, and it's built into the library rather than something to hand-roll. There is no JSON parsing code anywhere in this app.
- It reads a **standard kubeconfig file** the same way `kubectl` does (`KubernetesClientConfiguration.BuildDefaultConfig()` / `BuildConfigFromConfigFile()`), which is what makes the app generic — point it at any cluster's kubeconfig and it works unmodified.
- It supports the full API surface needed here out of the box: typed list operations, log streaming, and WebSocket-based `exec`.

### Interactive menu: Spectre.Console (not SharPrompt)
SharPrompt was the package named in the original ask, but **Spectre.Console** was chosen instead, and the ask explicitly invited a better alternative if one existed. Reasoning:
- **Actively maintained**, large community, frequent releases — SharPrompt has had long gaps with no updates.
- Spectre.Console includes everything SharPrompt offers for prompts/selection (`SelectionPrompt<T>`, `TextPrompt<T>`) **plus** rich output primitives this app actually needs: `Table` (for node/pod/service listings), `Rule`, `FigletText`, status spinners (`AnsiConsole.Status()`), and markup-colored text (`[green]Ready[/]`). Building the same experience with SharPrompt would have meant hand-rolling table rendering.
- Confirmed compatible with .NET 10 (targets `net10.0` cleanly, no compatibility shims needed) — verified by building the project, not just assumed.

---

## 2. Why the API client approach instead of shelling out to `kubectl`

The alternative design would be: run `kubectl get nodes -o json` as a subprocess, capture stdout, `JsonSerializer.Deserialize<...>` it into matching classes. That works, but:
- It requires `kubectl` to be installed and on `PATH` wherever this app runs — an extra dependency the API-client approach doesn't have.
- Every command needs its own subprocess + argument-string construction + stdout/stderr handling + JSON-shape assumptions that can drift between `kubectl` versions.
- `KubernetesClient` already **is** a typed C# representation of the exact same API `kubectl` calls — using it directly skips a whole layer of indirection and matches the "fetch cluster info from the dotnet app itself" framing in the ask much more literally: the app talks to the Kubernetes API, not to another CLI tool.

The one place a plain shell command remains conceptually relevant is `exec` — but even there, the app doesn't shell out; it opens the same WebSocket connection `kubectl exec` itself opens, via `IKubernetes.WebSocketNamespacedPodExecAsync`, and demultiplexes the stdout/stderr channels itself (`StreamDemuxer`).

---

## 3. No SSH, no login — how that's actually true here

Everything the app does is a call to the Kubernetes API server (`GET /api/v1/nodes`, `GET /api/v1/namespaces/{ns}/pods`, the pod `log` and `exec` subresources, etc.) — the same server `kubectl` talks to. At no point does the app open an SSH session to a node, to the cluster's host machine, or to anything else. This is also *why* "node logs" specifically isn't implemented as raw host log tailing (see §5) — genuine host-level logs are deliberately not exposed through the standard Kubernetes API on most clusters (including managed ones like EKS/GKE/AKS) for exactly this reason: getting them normally *requires* SSH/journalctl access to the node, which is the thing this app is explicitly trying to avoid needing.

### The one environment-specific wrinkle: reaching `k8slab` from Windows
This app runs on Windows. `k8slab`'s API server only lives inside the WSL2 network (`192.168.49.2:8443`), which Windows can't route to directly (the same problem solved earlier for the app's own frontend NodePort). This is a **local networking fact about this one cluster**, not a limitation of the app — against a normal cluster (a cloud cluster, a corporate cluster, Docker Desktop's Kubernetes, anything with a directly reachable API server) the app needs nothing extra. For `k8slab` specifically, a small bridge was needed — see §6.

---

## 4. Menu structure

Mapped directly onto the numbering from the ask, then extended with the same pattern ("similarly generate all the basic commands") to cover the other fundamentals people reach for constantly with `kubectl`:

```
Main Menu
├─ 1. Nodes
│   ├─ 1.1  List all nodes
│   ├─ 1.2  Node details (select a node)   — capacity, allocatable, conditions, addresses, labels, taints
│   └─ 1.3  Node events (select a node)    — see §5 for why this is "node logs" here
├─ 2. Pods
│   ├─ 2.1  List pods (select a namespace)
│   ├─ 2.2  Pod details (select namespace + pod)   — containers, images, container states, recent pod events
│   ├─ 2.3  Pod logs (select namespace + pod + container, choose tail length)
│   └─ 2.4  Pod exec (select namespace + pod + container, type a command)
├─ 3. Deployments  →  3.1  List deployments (select a namespace)
├─ 4. Services     →  4.1  List services (select a namespace)
├─ 5. Namespaces   →  5.1  List all namespaces
└─ 6. Events       →  6.1  List recent events (a namespace, or all)
```

Every selection — namespace, node, pod, container — is chosen from a **live list fetched from the cluster** via `SelectionPrompt<T>`, never typed freely. That's what makes "generic, works on any cluster/node/pod" concretely true rather than aspirational: there's no hardcoded name anywhere in the menu code.

---

## 5. Design decision: "node logs" → node Events

The ask specifically wanted "node logs if it is there." Investigated the honest answer: the Kubernetes API does not have a standard, universally-available "give me this node's log file" endpoint. (There's an undocumented kubelet debug proxy path some clusters expose, but it's inconsistent, often disabled, and clearly not "generic — works on any cluster.") The closest thing that **is** part of the standard API, always available, and genuinely useful is **Events** filtered to that node (`fieldSelector: involvedObject.kind=Node,involvedObject.name=<name>`) — the control plane's own record of what happened to that node (`NodeReady`, `NodeNotReady`, image pulls, etc.). Implemented as option 1.3, with an explicit in-app note explaining the substitution and why (rather than silently pretending it's the same thing as host logs).

Pod logs (2.3) are the real, direct equivalent for pods — `kubectl logs` and this app's implementation both stream the container's stdout/stderr straight from the API server, no substitution needed there.

---

## 6. Implementation walkthrough (in the order it happened)

### 6.1 Scaffolded the project
```powershell
dotnet new console -n K8sExplorer
dotnet add package KubernetesClient   # 19.0.2
dotnet add package Spectre.Console    # 0.57.2
```

### 6.2 Wrote the service layer first
`Services/KubernetesService.cs` — one method per operation (`GetNodesAsync`, `GetPodsAsync`, `GetPodLogsAsync`, `ExecInPodAsync`, etc.), each a thin, direct call into `IKubernetes`, returning the client library's own typed models. `Services/KubeClientFactory.cs` builds the `IKubernetes` client from a kubeconfig path (or the default, exactly like bare `kubectl`).

### 6.3 Wrote the display layer
`Display/Formatting.cs` — small helpers (`GetNodeStatus`, `GetNodeRoles`, `GetAge`, pod ready-count/restart-count) so the menu code reads like the thing it's displaying, matching what `kubectl get` columns show.

### 6.4 Wrote one menu class per resource type
`Menus/NodeMenu.cs`, `PodMenu.cs`, `DeploymentMenu.cs`, `ServiceMenu.cs`, `NamespaceMenu.cs`, `EventMenu.cs` — each owns its own `SelectionPrompt` loop and renders results as a Spectre `Table`.

### 6.5 Build errors hit and fixed
- `StreamType.Standard` doesn't exist on `KubernetesClient` 19.0.2's `StreamDemuxer` — the parameter has a working default, so the fix was simply not passing it (`new StreamDemuxer(webSocket)`).
- `GetValueOrDefault<TKey,TValue>` couldn't infer its type arguments against the K8s resource-quantity dictionaries — switched to explicit `TryGetValue` instead of the extension method.

Both were caught by `dotnet build` and fixed in the same pass — no runtime surprises from these two.

### 6.6 Verifying it against the real `k8slab` cluster
Spectre.Console's prompts need a real interactive terminal (arrow-key navigation) — running the app under a piped/non-TTY shell throws a clean `InvalidOperationException: Failed to read input in non-interactive mode` (confirmed this is Spectre correctly detecting the environment, not a bug). Since no `tmux` (or similar PTY driver) was available in this session to drive the real interactive UI, added a `--smoke-test` startup path (`SmokeTest.cs`) that calls the exact same `KubernetesService` methods the menus use, printing plain output — enough to prove the actual substance (cluster connectivity, correct typed data, log/exec output) against the live cluster, while being upfront that the interactive arrow-key experience itself wasn't driven by an automated test in this session (you'll be the first to actually navigate the menus).

Ran `dotnet run -- --smoke-test <kubeconfig>` against `k8slab` and confirmed, with real output:
- All 3 nodes listed correctly (name, Ready status, roles, container runtime).
- All 5 namespaces listed.
- All 7 pods in `product-catalog` listed with correct node placement and ready state.
- Real pod logs fetched and printed (the API's actual ASP.NET Core startup log lines).
- **Exec ran a real command inside a live pod** (`printenv MongoDbSettings__DatabaseName`) and printed its actual output (`ProductCatalog`) — see §6.7 for what it took to get this working.
- Node events fetched and printed.

### 6.7 The exec problem: `kubectl proxy` doesn't support WebSocket upgrades properly
First attempt bridged Windows → WSL2 via `kubectl proxy --port=8001` (a plain HTTP reverse proxy), with a minimal no-auth kubeconfig pointing at it. Nodes, pods, namespaces, and **logs** all worked perfectly through this. `exec` did not:
```
System.Net.WebSockets.WebSocketException: The server returned status code '403' when status code '101' was expected.
```
`kubectl proxy` is documented to have limited/unreliable support for the WebSocket protocol upgrade that `exec`/`attach`/`port-forward` subresources require — it's built for simple REST proxying, not full-duplex streaming. This wasn't a bug in the app's exec code; the same code path worked correctly once given a connection that actually preserves WebSocket semantics.

**Fix:** replaced the HTTP-proxy bridge with a **raw TCP tunnel** straight to the API server, using `kubectl port-forward`, which forwards bytes at the TCP level and therefore preserves TLS and WebSocket framing completely:
```bash
kubectl port-forward pod/kube-apiserver-k8slab 6443:8443 -n kube-system --address 0.0.0.0
```
(First tried `port-forward svc/kubernetes`, which failed — that Service has no pod selector, it's a manually-managed endpoints object, and `port-forward` needs a selector to find a backing pod. Targeting the API server's actual static pod directly in `kube-system` was the fix.)

Then generated a fully self-contained kubeconfig (real cluster CA + client cert/key, all embedded as base64 rather than referencing Linux file paths) pointing at the tunnel:
```bash
kubectl config view --raw --minify --flatten > kubeconfig-direct.yaml
# then rewrite its `server:` field to https://localhost:6443
```
`--flatten` was the important flag — without it, the exported kubeconfig references cert files by their WSL2 Linux paths (`/root/.minikube/...`), which don't exist on Windows. `--flatten` embeds the actual certificate bytes inline, making the file fully portable. Re-ran the smoke test against this and exec worked correctly.

Both bridge scripts are kept (`app/scripts/start-kubectl-proxy.sh` and `app/scripts/start-api-tcp-tunnel.sh`) since the simpler proxy is still perfectly fine for everything except exec, but the app's default kubeconfig now points at the full-featured tunnel.

---

## 7. Running it

**Interactive (the normal way):**
```powershell
cd C:\CodeBase\k8s-console-tool\K8sExplorer
dotnet run
```
It prompts for a kubeconfig path, defaulting to `../kubeconfig-direct.yaml` (this project's bridge into `k8slab`) if that file exists. Point it at any other cluster's kubeconfig to use it there instead — nothing else changes.

**Non-interactive verification:**
```powershell
dotnet run -- --smoke-test "C:\CodeBase\k8s-console-tool\kubeconfig-direct.yaml"
```

**Prerequisite for `k8slab` specifically** — the tunnel bridge must be running (it doesn't survive a WSL2 restart):
```powershell
wsl -d Ubuntu -u root -- bash /mnt/c/CodeBase/app/scripts/start-api-tcp-tunnel.sh
```

---

## 8. What's generic vs. what's specific to this local setup

| Generic (works against any cluster, unmodified) | Specific to `k8slab` on this machine |
|---|---|
| Every menu, every `KubernetesService` method, all typed models | The `kubeconfig-direct.yaml` / `kubeconfig-proxy.yaml` bridge files |
| Kubeconfig loading (`BuildDefaultConfig` / `BuildConfigFromConfigFile`) | The two tunnel scripts in `app/scripts/` |
| Namespace/node/pod/container selection (always live-fetched) | Nothing else — the app itself has zero `k8slab`-specific code |

## 9. Possible next steps

- **Resource usage** (CPU/memory actually in use, not just capacity) needs the separate `metrics.k8s.io` API (the `metrics-server` add-on) — not installed on `k8slab` yet; natural to wire up once it is.
- **Multi-context support** — list all contexts in a kubeconfig and let the user pick one at startup instead of assuming `current-context`.
- **Streaming/follow mode** for logs (`kubectl logs -f` equivalent) — currently fetches a fixed tail; the API supports a `follow` flag that could feed a live-updating Spectre `Live` display.
- **Full interactive exec shell** instead of one-shot commands — would mean wiring stdin into the exec WebSocket's stdin channel and handling terminal resize, a meaningfully bigger feature than "run one command and show the result."
