using k8s;
using k8s.Models;

namespace K8sDashboard.Api.Services;

// Same calls as k8s-console-tool's KubernetesService, generalized to take
// the IKubernetes client as a parameter instead of owning one — this
// backend serves many clusters, not one, but the actual Kubernetes-API
// logic underneath doesn't change at all between "one cluster" and "many
// clusters": it was already generic per-client, it just now takes that
// client explicitly.
public class KubernetesQueryService
{
    public async Task<IList<V1Node>> GetNodesAsync(IKubernetes client)
        => (await client.CoreV1.ListNodeAsync()).Items;

    public async Task<V1Node> GetNodeAsync(IKubernetes client, string name)
        => await client.CoreV1.ReadNodeAsync(name);

    public async Task<IList<V1Namespace>> GetNamespacesAsync(IKubernetes client)
        => (await client.CoreV1.ListNamespaceAsync()).Items;

    public async Task<IList<V1Pod>> GetPodsAsync(IKubernetes client, string namespaceName)
        => (await client.CoreV1.ListNamespacedPodAsync(namespaceName)).Items;

    public async Task<V1Pod> GetPodAsync(IKubernetes client, string namespaceName, string podName)
        => await client.CoreV1.ReadNamespacedPodAsync(podName, namespaceName);

    public async Task<IList<V1Deployment>> GetDeploymentsAsync(IKubernetes client, string namespaceName)
        => (await client.AppsV1.ListNamespacedDeploymentAsync(namespaceName)).Items;

    public async Task<IList<V1Service>> GetServicesAsync(IKubernetes client, string namespaceName)
        => (await client.CoreV1.ListNamespacedServiceAsync(namespaceName)).Items;

    public async Task<IList<Corev1Event>> GetEventsForObjectAsync(IKubernetes client, string kind, string name, string? namespaceName = null)
    {
        var fieldSelector = $"involvedObject.kind={kind},involvedObject.name={name}";
        var list = namespaceName is null
            ? await client.CoreV1.ListEventForAllNamespacesAsync(fieldSelector: fieldSelector)
            : await client.CoreV1.ListNamespacedEventAsync(namespaceName, fieldSelector: fieldSelector);
        return list.Items;
    }

    public async Task<IList<Corev1Event>> GetEventsAsync(IKubernetes client, string? namespaceName)
    {
        var list = namespaceName is null
            ? await client.CoreV1.ListEventForAllNamespacesAsync()
            : await client.CoreV1.ListNamespacedEventAsync(namespaceName);
        return list.Items;
    }

    public async Task<string> GetPodLogsAsync(IKubernetes client, string namespaceName, string podName, string containerName, int tailLines)
    {
        var stream = await client.CoreV1.ReadNamespacedPodLogAsync(
            name: podName,
            namespaceParameter: namespaceName,
            container: containerName,
            tailLines: tailLines);

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public async Task<string> ExecInPodAsync(IKubernetes client, string namespaceName, string podName, string containerName, string command)
    {
        using var webSocket = await client.WebSocketNamespacedPodExecAsync(
            name: podName,
            @namespace: namespaceName,
            command: new[] { "/bin/sh", "-c", command },
            container: containerName);

        var demuxer = new StreamDemuxer(webSocket);
        demuxer.Start();

        var stdOut = demuxer.GetStream(ChannelIndex.StdOut, ChannelIndex.StdOut);
        var stdErr = demuxer.GetStream(ChannelIndex.StdErr, ChannelIndex.StdErr);

        using var stdOutReader = new StreamReader(stdOut);
        using var stdErrReader = new StreamReader(stdErr);

        var stdOutTask = stdOutReader.ReadToEndAsync();
        var stdErrTask = stdErrReader.ReadToEndAsync();
        await Task.WhenAll(stdOutTask, stdErrTask);

        var combined = stdOutTask.Result + stdErrTask.Result;
        return combined.Length > 0 ? combined : "(no output)";
    }
}
