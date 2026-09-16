using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace PatchOrchestrator.Services;

// Every direct call this app makes to the Kubernetes API server lives here, in one place, so the
// workflow engine (PatchWorkflowEngine.cs) never has to know the mechanics of a cordon/taint/evict
// call - it just calls e.g. CordonAsync(...) and reacts to a plain result.
public class NodePatchOperations
{
    // The taint this app adds on top of cordoning, as a second, independent safety net right
    // before the patch window closes.
    //
    // NoSchedule (not NoExecute) is deliberate: NoSchedule only ever stops *new* pods landing on
    // the node - it does not forcibly remove anything already running there. By the time this
    // taint goes on, this app has already moved the workload pods off gracefully via
    // TryEvictPodAsync below. A NoExecute taint would also kick off DaemonSet pods (kube-proxy,
    // the CNI plugin, log shippers, etc.), which are supposed to keep running on a node right up
    // until the moment it actually reboots.
    private const string TaintKey = "patch-orchestrator/scheduled-patch";
    private const string TaintEffect = "NoSchedule";

    private readonly IKubernetes _client;
    private readonly ILogger<NodePatchOperations> _logger;

    public NodePatchOperations(IKubernetes client, ILogger<NodePatchOperations> logger)
    {
        _client = client;
        _logger = logger;
    }

    // Returns null (rather than throwing) if the node name doesn't exist in the cluster - a
    // typo'd ServerName in the schedule file is a common, expected failure mode, not a bug.
    public async Task<V1Node?> TryGetNodeAsync(string nodeName)
    {
        try
        {
            return await _client.CoreV1.ReadNodeAsync(nodeName);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Node {NodeName} was not found in the cluster", nodeName);
            return null;
        }
    }

    public static bool IsReady(V1Node node) =>
        node.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") ?? false;

    // The node's kernel/kubelet "boot ID" - changes across every real reboot. Comparing this
    // value before/after a patch window is how PatchWorkflowEngine tells "the 3rd party actually
    // rebooted this box" apart from "the node was Ready the whole time and nothing happened yet".
    public static string? GetBootId(V1Node node) => node.Status?.NodeInfo?.BootID;

    // Marks the node unschedulable ("cordon"). This single flag is what stops the Kubernetes
    // scheduler placing any *new* pod here - it does nothing to pods already running on the node.
    public async Task CordonAsync(string nodeName)
    {
        var patch = new V1Patch(new { spec = new { unschedulable = true } }, V1Patch.PatchType.MergePatch);
        await _client.CoreV1.PatchNodeAsync(patch, nodeName);
        _logger.LogInformation("Cordoned node {NodeName} (marked unschedulable - no new pods)", nodeName);
    }

    // Clears the unschedulable flag so the node is eligible for new pods again.
    public async Task UncordonAsync(string nodeName)
    {
        var patch = new V1Patch(new { spec = new { unschedulable = false } }, V1Patch.PatchType.MergePatch);
        await _client.CoreV1.PatchNodeAsync(patch, nodeName);
        _logger.LogInformation("Uncordoned node {NodeName} (schedulable again)", nodeName);
    }

    // Adds this app's taint if it isn't already there. Reads the node fresh first so this never
    // clobbers any *other* taints already present on the node.
    public async Task AddPatchTaintAsync(string nodeName)
    {
        var node = await _client.CoreV1.ReadNodeAsync(nodeName);
        var taints = node.Spec.Taints?.ToList() ?? new List<V1Taint>();

        if (taints.Any(t => t.Key == TaintKey))
        {
            return; // already applied on a previous tick - nothing to do
        }

        taints.Add(new V1Taint { Key = TaintKey, Effect = TaintEffect, Value = "true" });
        var patch = new V1Patch(new { spec = new { taints } }, V1Patch.PatchType.MergePatch);
        await _client.CoreV1.PatchNodeAsync(patch, nodeName);
        _logger.LogInformation("Tainted node {NodeName} ({Key}={Effect})", nodeName, TaintKey, TaintEffect);
    }

    // Removes only this app's taint, leaving every other taint on the node untouched.
    public async Task RemovePatchTaintAsync(string nodeName)
    {
        var node = await _client.CoreV1.ReadNodeAsync(nodeName);
        var taints = node.Spec.Taints?.Where(t => t.Key != TaintKey).ToList() ?? new List<V1Taint>();

        var patch = new V1Patch(new { spec = new { taints } }, V1Patch.PatchType.MergePatch);
        await _client.CoreV1.PatchNodeAsync(patch, nodeName);
        _logger.LogInformation("Removed patch taint from node {NodeName}", nodeName);
    }

    // Pods on this node that this app is allowed to gracefully migrate elsewhere. Deliberately
    // excludes:
    //   - DaemonSet pods: exactly one is supposed to run per node (kube-proxy, the CNI plugin,
    //     log agents, ...). Evicting one just makes Kubernetes recreate it on the very same node,
    //     so it isn't a "migration" and would achieve nothing.
    //   - Static/mirror pods (kubelet-managed, marked with the "kubernetes.io/config.mirror"
    //     annotation): these are not part of the API server's scheduling at all, and the API
    //     server itself rejects evicting them.
    //   - Bare pods with no owning controller: nothing would recreate these elsewhere, so
    //     evicting one would simply delete it forever rather than migrate it. Left in place and
    //     flagged separately (see PatchWorkflowEngine) so a human can decide what to do with them.
    //   - Pods that are already finished (Succeeded/Failed) or already terminating.
    public async Task<List<V1Pod>> GetMigratablePodsAsync(string nodeName)
    {
        var podsOnNode = await _client.CoreV1.ListPodForAllNamespacesAsync(fieldSelector: $"spec.nodeName={nodeName}");

        return podsOnNode.Items.Where(pod =>
        {
            var isDaemonSetOwned = pod.Metadata.OwnerReferences?.Any(o => o.Kind == "DaemonSet") ?? false;
            var isStaticMirrorPod = pod.Metadata.Annotations?.ContainsKey("kubernetes.io/config.mirror") ?? false;
            var hasNoController = (pod.Metadata.OwnerReferences?.Count ?? 0) == 0;
            var isAlreadyFinished = pod.Status?.Phase is "Succeeded" or "Failed";
            var isTerminating = pod.Metadata.DeletionTimestamp.HasValue;

            return !isDaemonSetOwned && !isStaticMirrorPod && !hasNoController && !isAlreadyFinished && !isTerminating;
        }).ToList();
    }

    // Bare pods (no owning controller) sitting on this node - not evicted automatically (see
    // GetMigratablePodsAsync), just reported so PatchWorkflowEngine can warn about them.
    public async Task<List<V1Pod>> GetBarePodsAsync(string nodeName)
    {
        var podsOnNode = await _client.CoreV1.ListPodForAllNamespacesAsync(fieldSelector: $"spec.nodeName={nodeName}");
        return podsOnNode.Items
            .Where(pod => (pod.Metadata.OwnerReferences?.Count ?? 0) == 0
                && pod.Status?.Phase is not ("Succeeded" or "Failed"))
            .ToList();
    }

    // Gracefully evicts one pod using the Kubernetes Eviction API - the same mechanism
    // `kubectl drain` itself uses. Unlike a plain delete, an eviction:
    //   - respects the pod's own terminationGracePeriodSeconds (a clean shutdown, not a sudden
    //     kill - containers get their normal chance to finish in-flight work and exit cleanly), and
    //   - is REFUSED by the API server (HTTP 429) if it would violate a PodDisruptionBudget the
    //     pod's owner has configured. That's Kubernetes itself protecting the application's own
    //     "always keep at least N replicas up" rule - we treat a 429 as "try again next tick",
    //     not as an error.
    // Returns true if the eviction was accepted, false if it was blocked.
    public async Task<bool> TryEvictPodAsync(V1Pod pod)
    {
        var podName = pod.Metadata.Name;
        var podNamespace = pod.Metadata.NamespaceProperty;

        var eviction = new V1Eviction
        {
            ApiVersion = "policy/v1",
            Kind = "Eviction",
            Metadata = new V1ObjectMeta { Name = podName, NamespaceProperty = podNamespace }
        };

        try
        {
            await _client.CoreV1.CreateNamespacedPodEvictionAsync(eviction, podName, podNamespace);
            _logger.LogInformation("Evicted pod {Namespace}/{Pod} for graceful migration off the node", podNamespace, podName);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning(
                "Eviction of {Namespace}/{Pod} was blocked by a PodDisruptionBudget - will retry on a later check",
                podNamespace, podName);
            return false;
        }
    }
}
