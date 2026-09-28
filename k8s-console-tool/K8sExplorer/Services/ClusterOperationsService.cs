using System.Net;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace K8sExplorer.Services;

// Everything in the app that CHANGES cluster state lives here, kept apart
// from the read-only KubernetesService so the write surface (and the RBAC it
// needs — see deploy/rbac.yaml) is easy to see at a glance. Each method is
// the API-level equivalent of a kubectl command, noted alongside it.
public class ClusterOperationsService
{
    private readonly IKubernetes _client;

    public ClusterOperationsService(IKubernetes client)
    {
        _client = client;
    }

    // ---------------------------------------------------------------- nodes

    // kubectl cordon / uncordon
    public Task SetNodeUnschedulableAsync(string nodeName, bool unschedulable) =>
        _client.CoreV1.PatchNodeAsync(
            new V1Patch(new { spec = new { unschedulable } }, V1Patch.PatchType.MergePatch),
            nodeName);

    // kubectl taint nodes <node> key[=value]:Effect — replaces any existing
    // taint with the same key+effect, as kubectl --overwrite does.
    public async Task AddOrUpdateTaintAsync(string nodeName, V1Taint taint)
    {
        var node = await _client.CoreV1.ReadNodeAsync(nodeName);
        var taints = (node.Spec?.Taints ?? new List<V1Taint>())
            .Where(t => !(t.Key == taint.Key && t.Effect == taint.Effect))
            .Append(taint)
            .ToList();
        await PatchTaintsAsync(node, taints);
    }

    // kubectl taint nodes <node> key[:Effect]- ; effect null removes every
    // taint with that key. Returns false if nothing matched.
    public async Task<bool> RemoveTaintAsync(string nodeName, string key, string? effect)
    {
        var node = await _client.CoreV1.ReadNodeAsync(nodeName);
        var existing = node.Spec?.Taints ?? new List<V1Taint>();
        var remaining = existing
            .Where(t => !(t.Key == key && (effect is null || t.Effect == effect)))
            .ToList();
        if (remaining.Count == existing.Count) return false;

        await PatchTaintsAsync(node, remaining);
        return true;
    }

    // Merge patch replaces the whole taints list, so resourceVersion is
    // included: if the node changed since we read it the API server returns
    // 409 Conflict instead of silently dropping someone else's taint.
    private Task PatchTaintsAsync(V1Node node, List<V1Taint> taints) =>
        _client.CoreV1.PatchNodeAsync(
            new V1Patch(
                new { metadata = new { resourceVersion = node.Metadata.ResourceVersion }, spec = new { taints } },
                V1Patch.PatchType.MergePatch),
            node.Metadata.Name);

    // kubectl label nodes <node> key=value / key-
    public Task SetNodeLabelAsync(string nodeName, string key, string? value) =>
        _client.CoreV1.PatchNodeAsync(LabelPatch(key, value), nodeName);

    // kubectl drain <node> [--ignore-daemonsets] [--delete-emptydir-data] [--force]
    //
    // Same algorithm as kubectl: cordon, work out which pods may be removed
    // (refusing the whole drain if any pod is blocked, so nothing is evicted
    // half-way), evict them through the Eviction API so PodDisruptionBudgets
    // are honoured, then wait for them to actually terminate.
    public async Task<DrainResult> DrainNodeAsync(string nodeName, DrainOptions options, Action<string> log, CancellationToken ct = default)
    {
        var result = new DrainResult();

        await SetNodeUnschedulableAsync(nodeName, true);
        log($"cordoned {nodeName}");

        var pods = (await _client.CoreV1.ListPodForAllNamespacesAsync(
            fieldSelector: $"spec.nodeName={nodeName}", cancellationToken: ct)).Items;

        var toEvict = new List<V1Pod>();
        foreach (var pod in pods)
        {
            var id = $"{pod.Metadata.NamespaceProperty}/{pod.Metadata.Name}";
            var controller = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
            var finished = pod.Status?.Phase is "Succeeded" or "Failed";

            if (pod.Metadata.Annotations?.ContainsKey("kubernetes.io/config.mirror") == true)
            {
                result.Skipped.Add($"{id} (static/mirror pod — managed by the kubelet)");
                continue;
            }
            if (controller?.Kind == "DaemonSet" && !finished)
            {
                if (options.IgnoreDaemonSets)
                    result.Skipped.Add($"{id} (DaemonSet-managed)");
                else
                    result.Blocked.Add($"{id} is DaemonSet-managed (enable 'ignore DaemonSets')");
                continue;
            }
            if (controller is null && !finished && !options.Force)
            {
                result.Blocked.Add($"{id} has no controller and would be lost for good (enable 'force')");
                continue;
            }
            if (pod.Spec?.Volumes?.Any(v => v.EmptyDir is not null) == true && !finished && !options.DeleteEmptyDirData)
            {
                result.Blocked.Add($"{id} uses emptyDir storage that would be deleted (enable 'delete emptyDir data')");
                continue;
            }
            toEvict.Add(pod);
        }

        if (result.Blocked.Count > 0)
        {
            log("drain aborted before evicting anything; node left cordoned");
            return result;
        }

        var deadline = DateTime.UtcNow + options.Timeout;

        foreach (var pod in toEvict)
        {
            var id = $"{pod.Metadata.NamespaceProperty}/{pod.Metadata.Name}";
            if (await EvictWithRetryAsync(pod, deadline, log, ct))
                result.Evicted.Add(id);
            else
                result.Failed.Add($"{id}: still blocked by a PodDisruptionBudget at timeout");
        }

        // Wait for the evicted pods to be gone. A controller may recreate a
        // pod with the same name elsewhere (StatefulSets), so compare UIDs.
        var pending = toEvict.Where(p => result.Evicted.Contains($"{p.Metadata.NamespaceProperty}/{p.Metadata.Name}")).ToList();
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            var stillThere = new List<V1Pod>();
            foreach (var pod in pending)
            {
                try
                {
                    var current = await _client.CoreV1.ReadNamespacedPodAsync(pod.Metadata.Name, pod.Metadata.NamespaceProperty, cancellationToken: ct);
                    if (current.Metadata.Uid == pod.Metadata.Uid) stillThere.Add(pod);
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }
            pending = stillThere;
            if (pending.Count > 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        foreach (var pod in pending)
            result.Failed.Add($"{pod.Metadata.NamespaceProperty}/{pod.Metadata.Name}: evicted but still terminating at timeout");

        log(result.Failed.Count == 0 ? $"{nodeName} drained" : $"{nodeName} drain finished with {result.Failed.Count} problem(s)");
        return result;
    }

    // Returns false only if a PodDisruptionBudget kept refusing (HTTP 429)
    // until the deadline. A pod that's already gone counts as evicted.
    private async Task<bool> EvictWithRetryAsync(V1Pod pod, DateTime deadline, Action<string> log, CancellationToken ct)
    {
        var id = $"{pod.Metadata.NamespaceProperty}/{pod.Metadata.Name}";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EvictPodAsync(pod.Metadata.NamespaceProperty, pod.Metadata.Name);
                log($"evicting {id}");
                return true;
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (DateTime.UtcNow >= deadline) return false;
                log($"{id} blocked by a PodDisruptionBudget, retrying in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    // ----------------------------------------------------------------- pods

    // kubectl delete pod [--grace-period=0 --force]. For a pod owned by a
    // controller, this is the usual "restart this pod" — it gets recreated.
    public Task DeletePodAsync(string namespaceName, string podName, bool force) =>
        _client.CoreV1.DeleteNamespacedPodAsync(podName, namespaceName, gracePeriodSeconds: force ? 0 : null);

    // Eviction API — like delete, but refused (HTTP 429) if it would violate
    // a PodDisruptionBudget. This is what drain uses under the hood.
    public Task EvictPodAsync(string namespaceName, string podName) =>
        _client.CoreV1.CreateNamespacedPodEvictionAsync(
            new V1Eviction { Metadata = new V1ObjectMeta { Name = podName, NamespaceProperty = namespaceName } },
            podName,
            namespaceName);

    // kubectl label pod <pod> key=value / key-
    public Task SetPodLabelAsync(string namespaceName, string podName, string key, string? value) =>
        _client.CoreV1.PatchNamespacedPodAsync(LabelPatch(key, value), podName, namespaceName);

    // Walks ownerReferences up to the workload a person would actually
    // restart: Pod -> ReplicaSet -> Deployment, or Pod -> StatefulSet /
    // DaemonSet directly. Null for bare pods and anything else (Jobs, ...).
    public async Task<WorkloadRef?> GetOwningWorkloadAsync(V1Pod pod)
    {
        var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
        var ns = pod.Metadata.NamespaceProperty;
        switch (owner?.Kind)
        {
            case "StatefulSet":
            case "DaemonSet":
                return new WorkloadRef(owner.Kind, ns, owner.Name);
            case "ReplicaSet":
                var rs = await _client.AppsV1.ReadNamespacedReplicaSetAsync(owner.Name, ns);
                var deployment = rs.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == "Deployment");
                return deployment is null ? null : new WorkloadRef("Deployment", ns, deployment.Name);
            default:
                return null;
        }
    }

    // kubectl rollout restart — bumps a pod-template annotation, which makes
    // the controller roll every pod using its normal update strategy
    // (so no downtime for a Deployment with enough replicas).
    public Task RestartWorkloadAsync(WorkloadRef workload)
    {
        var patch = new V1Patch(
            new
            {
                spec = new
                {
                    template = new
                    {
                        metadata = new
                        {
                            annotations = new Dictionary<string, string>
                            {
                                ["kubectl.kubernetes.io/restartedAt"] = DateTime.UtcNow.ToString("o")
                            }
                        }
                    }
                }
            },
            V1Patch.PatchType.MergePatch);

        return workload.Kind switch
        {
            "Deployment" => _client.AppsV1.PatchNamespacedDeploymentAsync(patch, workload.Name, workload.Namespace),
            "StatefulSet" => _client.AppsV1.PatchNamespacedStatefulSetAsync(patch, workload.Name, workload.Namespace),
            "DaemonSet" => _client.AppsV1.PatchNamespacedDaemonSetAsync(patch, workload.Name, workload.Namespace),
            _ => throw new NotSupportedException($"Cannot rollout-restart a {workload.Kind}")
        };
    }

    // A null value in a JSON merge patch deletes the key. JsonObject is used
    // rather than an anonymous type so the null is written out, not skipped.
    private static V1Patch LabelPatch(string key, string? value) =>
        new(
            new JsonObject { ["metadata"] = new JsonObject { ["labels"] = new JsonObject { [key] = value } } },
            V1Patch.PatchType.MergePatch);
}

public record WorkloadRef(string Kind, string Namespace, string Name)
{
    public override string ToString() => $"{Kind} {Namespace}/{Name}";
}

public record DrainOptions(bool IgnoreDaemonSets, bool DeleteEmptyDirData, bool Force, TimeSpan Timeout);

public class DrainResult
{
    public List<string> Evicted { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Blocked { get; } = new();
    public List<string> Failed { get; } = new();
    public bool Succeeded => Blocked.Count == 0 && Failed.Count == 0;
}
