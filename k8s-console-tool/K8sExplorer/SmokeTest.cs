using K8sExplorer.Services;

// Plain-output verification path (see Program.cs --smoke-test). Exercises
// the real KubernetesService methods against a real cluster: node listing,
// namespace listing, pod listing in a live namespace, and fetching real pod
// logs — the same calls every interactive menu option ultimately makes.
internal static class SmokeTest
{
    public static async Task RunAsync(string kubeconfigPath)
    {
        Console.WriteLine($"Using kubeconfig: {kubeconfigPath}");
        var client = KubeClientFactory.Create(kubeconfigPath);
        var k8s = new KubernetesService(client);

        Console.WriteLine("\n=== Nodes ===");
        var nodes = await k8s.GetNodesAsync();
        foreach (var node in nodes)
        {
            var status = K8sExplorer.Display.Formatting.GetNodeStatus(node);
            var roles = K8sExplorer.Display.Formatting.GetNodeRoles(node);
            Console.WriteLine($"{node.Metadata.Name,-16} {status,-10} roles={roles,-16} runtime={node.Status?.NodeInfo?.ContainerRuntimeVersion}");
        }

        Console.WriteLine("\n=== Namespaces ===");
        var namespaces = await k8s.GetNamespacesAsync();
        foreach (var ns in namespaces)
            Console.WriteLine($"{ns.Metadata.Name,-20} {ns.Status?.Phase}");

        Console.WriteLine("\n=== Pods in 'product-catalog' ===");
        var pods = await k8s.GetPodsAsync("product-catalog");
        foreach (var pod in pods)
        {
            var ready = K8sExplorer.Display.Formatting.GetPodReadyCount(pod);
            Console.WriteLine($"{pod.Metadata.Name,-40} ready={ready,-5} node={pod.Spec?.NodeName,-14} phase={pod.Status?.Phase}");
        }

        if (pods.Count > 0)
        {
            var samplePod = pods.First(p => p.Metadata.Name.StartsWith("product-api"));
            var container = samplePod.Spec.Containers[0].Name;
            Console.WriteLine($"\n=== Last 10 log lines: {samplePod.Metadata.Name} / {container} ===");
            var logs = await k8s.GetPodLogsAsync("product-catalog", samplePod.Metadata.Name, container, 10);
            Console.WriteLine(logs);

            Console.WriteLine($"=== Exec 'printenv MongoDbSettings__DatabaseName' in {samplePod.Metadata.Name} ===");
            var execOutput = await k8s.ExecInPodAsync("product-catalog", samplePod.Metadata.Name, container, "printenv MongoDbSettings__DatabaseName");
            Console.WriteLine(execOutput);

            Console.WriteLine($"=== Recent events for node: {nodes[0].Metadata.Name} ===");
            var events = await k8s.GetEventsForObjectAsync("Node", nodes[0].Metadata.Name);
            Console.WriteLine(events.Count == 0 ? "(none)" : string.Join("\n", events.Select(e => $"{e.Reason}: {e.Message}")));
        }

        Console.WriteLine("\nSmoke test completed successfully.");
    }
}
