using System.Collections.Concurrent;
using System.Text;
using k8s;
using K8sDashboard.Api.Services;

namespace K8sDashboard.Api.Services;

// Builds (and caches) an IKubernetes client per registered cluster. One
// client per cluster id is reused across requests rather than rebuilt every
// time — building parses the kubeconfig and sets up an HttpClient, which is
// meant to be long-lived, exactly like `new HttpClient()` itself.
public class ClusterClientFactory
{
    private readonly ClusterConfigStore _store;
    private readonly ConcurrentDictionary<Guid, IKubernetes> _clients = new();

    public ClusterClientFactory(ClusterConfigStore store)
    {
        _store = store;
    }

    public async Task<IKubernetes> GetClientAsync(Guid clusterId)
    {
        if (_clients.TryGetValue(clusterId, out var existing))
            return existing;

        var config = await _store.GetByIdAsync(clusterId)
            ?? throw new KeyNotFoundException($"No cluster registered with id '{clusterId}'.");

        var client = BuildClient(config.KubeconfigYaml);
        _clients[clusterId] = client;
        return client;
    }

    // Call after a cluster's kubeconfig is updated/deleted so the next
    // request builds a fresh client instead of reusing a stale connection.
    public void Invalidate(Guid clusterId) => _clients.TryRemove(clusterId, out _);

    private static IKubernetes BuildClient(string kubeconfigYaml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kubeconfigYaml));
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }
}
