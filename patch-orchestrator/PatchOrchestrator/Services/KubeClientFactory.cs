using k8s;

namespace PatchOrchestrator.Services;

// Builds the one Kubernetes API client this app uses, from a kubeconfig file - exactly the same
// way k8s-console-tool and k8s-dashboard do it, and exactly the same way `kubectl` itself does it.
// This app never SSHes into a node or the cluster host; every single thing it does is a normal
// call to the Kubernetes API server using the credentials in that kubeconfig.
public static class KubeClientFactory
{
    public static IKubernetes BuildFromConfig(IConfiguration configuration)
    {
        var kubeconfigPath = configuration["PatchOrchestrator:KubeconfigPath"];

        var config = string.IsNullOrWhiteSpace(kubeconfigPath)
            // Nothing configured -> same default kubectl itself falls back to (~/.kube/config).
            ? KubernetesClientConfiguration.BuildDefaultConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath);

        return new Kubernetes(config);
    }
}
