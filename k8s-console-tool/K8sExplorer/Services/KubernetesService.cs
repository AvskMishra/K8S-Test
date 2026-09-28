using k8s;
using k8s.Models;

namespace K8sExplorer.Services;

// Thin wrapper around the official Kubernetes C# client. Every method returns
// the client library's own strongly-typed model classes (V1Node, V1Pod, ...)
// — the client deserializes the API server's JSON into these directly, so
// there's no manual JSON parsing anywhere in this app, and no shelling out
// to `kubectl`. This talks straight to the API server, exactly like kubectl
// itself does, using whatever kubeconfig it's given — nothing here is tied
// to any specific cluster.
public class KubernetesService
{
    private readonly IKubernetes _client;

    public KubernetesService(IKubernetes client)
    {
        _client = client;
    }

    public async Task<IList<V1Node>> GetNodesAsync()
    {
        var list = await _client.CoreV1.ListNodeAsync();
        return list.Items;
    }

    public async Task<IList<V1Namespace>> GetNamespacesAsync()
    {
        var list = await _client.CoreV1.ListNamespaceAsync();
        return list.Items;
    }

    public Task<V1Node> GetNodeAsync(string name) => _client.CoreV1.ReadNodeAsync(name);

    public async Task<IList<V1Pod>> GetPodsAsync(string namespaceName)
    {
        var list = await _client.CoreV1.ListNamespacedPodAsync(namespaceName);
        return list.Items;
    }

    // namespaceName null = all namespaces; nodeName narrows to pods scheduled
    // on that node (the same field selector kubectl uses for --field-selector).
    public async Task<IList<V1Pod>> FindPodsAsync(string? namespaceName, string? nodeName)
    {
        var fieldSelector = nodeName is null ? null : $"spec.nodeName={nodeName}";
        var list = namespaceName is null
            ? await _client.CoreV1.ListPodForAllNamespacesAsync(fieldSelector: fieldSelector)
            : await _client.CoreV1.ListNamespacedPodAsync(namespaceName, fieldSelector: fieldSelector);
        return list.Items;
    }

    public Task<V1Pod> GetPodAsync(string namespaceName, string name) =>
        _client.CoreV1.ReadNamespacedPodAsync(name, namespaceName);

    public async Task<IList<V1Deployment>> GetDeploymentsAsync(string namespaceName)
    {
        var list = await _client.AppsV1.ListNamespacedDeploymentAsync(namespaceName);
        return list.Items;
    }

    public async Task<IList<V1Service>> GetServicesAsync(string namespaceName)
    {
        var list = await _client.CoreV1.ListNamespacedServiceAsync(namespaceName);
        return list.Items;
    }

    // Kubernetes Events are the closest generic, always-available substitute
    // for "node logs" via the API server. Actual host-level kubelet/syslog
    // logs are deliberately NOT exposed through the standard API for
    // security reasons (that's what SSH/journalctl on the node itself is
    // for) — Events are what the control plane itself observed about the
    // object, which works identically on any cluster including managed ones
    // (EKS/GKE/AKS) where node-level log access is usually locked down.
    public async Task<IList<Corev1Event>> GetEventsForObjectAsync(string kind, string name, string? namespaceName = null)
    {
        var fieldSelector = $"involvedObject.kind={kind},involvedObject.name={name}";
        var list = namespaceName is null
            ? await _client.CoreV1.ListEventForAllNamespacesAsync(fieldSelector: fieldSelector)
            : await _client.CoreV1.ListNamespacedEventAsync(namespaceName, fieldSelector: fieldSelector);
        return list.Items;
    }

    public async Task<IList<Corev1Event>> GetEventsAsync(string? namespaceName)
    {
        var list = namespaceName is null
            ? await _client.CoreV1.ListEventForAllNamespacesAsync()
            : await _client.CoreV1.ListNamespacedEventAsync(namespaceName);
        return list.Items;
    }

    public async Task<string> GetPodLogsAsync(string namespaceName, string podName, string containerName, int tailLines)
    {
        var stream = await _client.CoreV1.ReadNamespacedPodLogAsync(
            name: podName,
            namespaceParameter: namespaceName,
            container: containerName,
            tailLines: tailLines);

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    // Runs a single non-interactive command inside a container and returns
    // whatever it printed to stdout/stderr. Not a full interactive shell —
    // deliberately kept simple, matching "see the log/output in the console"
    // rather than building a terminal emulator.
    public Task<string> ExecInPodAsync(string namespaceName, string podName, string containerName, string command) =>
        ExecInPodAsync(namespaceName, podName, containerName, new[] { "/bin/sh", "-c", command });

    // argv form: runs the program directly, no shell (like `kubectl exec -- cmd args`).
    public async Task<string> ExecInPodAsync(string namespaceName, string podName, string containerName, string[] command)
    {
        var output = new System.Text.StringBuilder();

        using var webSocket = await _client.WebSocketNamespacedPodExecAsync(
            name: podName,
            @namespace: namespaceName,
            command: command,
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

        if (!string.IsNullOrEmpty(stdOutTask.Result))
            output.Append(stdOutTask.Result);
        if (!string.IsNullOrEmpty(stdErrTask.Result))
            output.Append(stdErrTask.Result);

        return output.Length > 0 ? output.ToString() : "(no output)";
    }
}
